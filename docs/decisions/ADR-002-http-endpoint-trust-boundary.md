# ADR-002：HTTP 入口的信任边界与失败语义

- 状态：已接受（2026-10-23）
- 决策者：项目所有者

## 背景

最初的 `MapAgentHarness` 直接把请求体绑定为 `AgentTurnRequest`，然后调用 `IAgentEngine`。这带来三个问题：

1. 客户端可以伪造 `TenantId` / `UserId`，而工具内部重新授权依赖这两个值；
2. 客户端可以覆盖 `SystemPrompt` / `MaxToolRounds`，绕过宿主配置；
3. 端点绕过 `TurnPipeline`，预算预留/结算与审计没有生效。

同时，P0 spike 暴露了失败路径：模型错误会直接从引擎抛出，SSE 没有 `error` 事件；达到最大工具轮次时返回空文本 + `tool_calls`，宿主无法区分“完成”和“未完成”。

## 决策

1. **请求体只承载业务输入。** `MapAgentHarness` 绑定 `AgentChatRequest`：`Input`、`ConversationId`、`PageContext`。租户、用户、历史、系统提示、轮次上限一律由服务端组装。
2. **身份只来自 Claims。** 默认 `ClaimsAgentTurnFactory` 从 `HttpContext.User` 读取用户/租户 Claim；缺失时返回 401。宿主可用自定义 `IAgentTurnFactory` 替换，但不能退回请求体身份。
3. **服务端配置承载系统提示与轮次。** `AgentHarnessEndpointOptions` 提供 `SystemPrompt`、`MaxToolRounds`、`HistoryLimit` 与 Claim 类型；通过 `AddAgentHarnessAspNetCore` 注册。
4. **端点必须走 `TurnPipeline`。** 预算/审计是默认路径，不是可选项。SSE 端点不再直接注入 `IAgentEngine`。
5. **失败以 `AgentEvent.Failed` 进入事件流。** 新增 `ChatModelException`（稳定 `Code`、安全 `Message`、`IsTransient`）；`TurnPipeline` 把引擎异常转换为 `Failed`。取消仍按取消异常向上传播；引擎内部超时（宿主未取消）映射为 `timeout`。
6. **最大轮次是显式的未完成。** 未执行的 tool call 补 `ToolFinished(Ok=false)`，并把 `AgentTurnResult.FinishReason` 设为 `max_tool_rounds`；`TurnPipeline` 按未完成结算与审计。

## 后果

- 正面：身份不可由请求体伪造；预算/审计不可绕过；SSE 不会无事件断流；宿主能稳定区分成功、失败与未完成。
- 负面：这是破坏性 API 变更，宿主必须调用 `AddAgentHarnessAspNetCore`、配置 Claim 类型与系统提示；已有直接绑定 `AgentTurnRequest` 的宿主需要改代码。
- 剩余：`ClaimsAgentTurnFactory` 只校验 Claim 存在与非空，不校验 conversation 归属；宿主必须在 `IConversationStore` 或自定义 factory 中做数据范围校验。

## 考虑过的替代

| 方案 | 未选原因 |
|---|---|
| 继续绑定 `AgentTurnRequest`，在端点里覆盖字段 | 仍然鼓励客户端传身份；容易在新增字段时再次漏掉 |
| 只在文档里要求宿主加 `RequireAuthorization` | 认证不等于授权；请求体身份仍然是伪造入口 |
| 每个宿主自己写端点 | 重复实现 SSE、错误映射、预算/审计接线，安全默认值不一致 |
| 把 `ClaimsPrincipal` 放进 Abstractions | Abstractions 必须保持 BCL-only；HTTP 身份属于 AspNetCore 层 |

## 复审条件

- 宿主出现非 Claims 的身份来源（例如 mTLS、API key）；
- 需要支持多租户共享会话或跨租户管理视图；
- 端点协议从 SSE 扩展为双向通道。
