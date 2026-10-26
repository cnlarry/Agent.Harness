# AGENTS.md — Agent Harness 开发指引

> 面向在本仓库工作的 AI/人类开发者。开工前至少读：本文件、[README.md](README.md)、[docs/blueprint.md](docs/blueprint.md)、[docs/architecture.md](docs/architecture.md)。

## 1. 项目是什么

Agent Harness 是**企业管理系统里的受控 Agent 运行时**：MAF/其他引擎负责跑 Agent，Harness 负责权限之外的治理壳——工具白名单、策略、预算、审计、会话、SSE。

**它不是** MAF 的 `HarnessAgent`，也不采用 MAF 的 Hosting / Durable / Harness。命名冲突的解释见 [ADR-001](docs/decisions/ADR-001-name-and-scope.md)。

## 2. 命名与目录

- 解决方案：`Agent.Harness.slnx`
- 命名空间/包：`Agent.Harness.*`
- 项目目录：`src/Agent.Harness.<Area>`、`tests/Agent.Harness.Tests`、`samples/Agent.Harness.Sample.*`
- 不要改成 `AgentHarness`、`AgentKit`、`Harness` 等旧名；改名前先写 ADR。

## 3. 构建与验证

```powershell
dotnet build Agent.Harness.slnx
dotnet test tests\Agent.Harness.Tests\Agent.Harness.Tests.csproj
dotnet run --project samples\Agent.Harness.Sample.Console\Agent.Harness.Sample.Console.csproj
```

跨引擎 parity（对比工具，不是门禁）：`dotnet run --project tools\Agent.Harness.Parity -- run|compare`，
跑法与判定口径见 [tools/Agent.Harness.Parity/README.md](tools/Agent.Harness.Parity/README.md)。

- 目标框架：库 `net8.0;net10.0`；测试/示例 `net10.0`。
- `TreatWarningsAsErrors=true`；新增警告必须修掉，不要用 `NoWarn` 掩盖（`CS1591` 是骨架期临时豁免，稳定后移除）。
- 不要启动长驻服务；验证用 `dotnet test` 或短命 console sample。
- 本机可能是 Windows + WDAC：`dotnet build` 通过不等于可运行；需要运行验证时先确认 DLL 能加载。

## 4. 依赖硬规则

| 项目 | 允许 | 禁止 |
|---|---|---|
| `Abstractions` | BCL | 任何 Agent 框架、ASP.NET Core |
| `Core` | Abstractions | MAF、ASP.NET Core |
| `Maf` | Abstractions、Core、MAF 核心 | ASP.NET Core、业务系统 |
| `AspNetCore` | Abstractions、Core、ASP.NET Core | MAF |
| `Testing` | Abstractions、Core | 真实网络/数据库 |

**MAF 类型（`AIAgent`、`ChatMessage`、`AgentSession`、`AIFunction` 等）只允许出现在 `Agent.Harness.Maf`。** 控制器、领域服务、工具实现不得引用 `Microsoft.Agents.AI`。

`spikes/` 只允许引用 Abstractions/Core/Maf/Testing，不参与打包；验证过的代码要先提升到 `src/`，再按上表约束。

## 5. 安全不变式

1. 认证/授权由宿主负责；`MapAgentHarness` 不做认证。
2. 工具执行时必须按当前用户重新授权；`IToolPolicy` 只是执行前门禁，不是权限依据。
3. 工具 Schema 由服务端硬编码，绝不接受客户端传入。
4. 模型输出永不进入 SQL/表达式解析器。
5. 密钥只放环境变量/密钥库；日志与消息落库前脱敏。
6. 高风险写动作要有结构化确认，不能靠自然语言“好的”。
7. 预算预留/结算与审计不可绕过；`TurnPipeline` 默认包住。
8. 会话归属必须由 `IConversationStore.IsOwnedByAsync` 校验；跨用户访问返回 403，不能只靠空历史。

## 6. 当前阶段

- 已完成：契约、`ToolRegistry`、`TurnPipeline`、MAF 适配骨架、SSE 端点、测试替身、console sample；P0 spike（MAF 1.25 + `deepseek-flash` + 3 工具）10/10；失败事件、`max_tool_rounds`、Claims 身份组装与 `TurnPipeline` 接线；`IChatModel` 录制/回放 harness + 50 条构造 transcript 回放；工具 Schema 逐字快照与越权/泄露用例；SSE 失败端到端与适配器 reasoning/usage/取消/异常契约；`IConversationStore.IsOwnedByAsync` 会话归属校验与 403；`MafAgentEngineOptions` 只读 allowlist、DI 默认只读、并发默认 false、IConfiguration 绑定、空正文重试；P2 100 次真实基线（文本 p50 749 / 工具 1717 ms，整体 p95 2081 ms）；**跨引擎 parity 工具**（`tools/Agent.Harness.Parity`：归一化结果比对、三档判定）与 EOS 旧引擎侧回放（合成 2 条 + 从库内真实会话重建 10 条，全 PASS）；顺带修掉 `ChatModelChatClient` 丢弃 `ChatOptions.Instructions` 的缺陷，并补上收尾轮说明（`MafAgentEngineOptions.FinalRoundInstruction`）；`build` 0 警告 0 错误，`test` 118/118。
- 未完成：**进程内流式录制**（分片、轮次边界、系统提示词快照——现在靠库内会话重建，这三样拿不到）、读路径灰度、写路径、抽包、工作流。
- 50 条回放是构造场景，不是 EOS 真机录制；P0 是独立 spike，不替代 EOS parity。`MafAgentEngine` 仍未达到生产可用的 parity/灰度证据。
- **不要**在没有 EOS parity 与灰度证据的情况下把 `MafAgentEngine` 宣传成生产可用。

## 7. 代码约定

- 文件级 namespace；`record` 表达不可变契约；`sealed` 优先。
- 公开 API 尽量 XML 注释；骨架期 `CS1591` 暂豁免。
- 异步方法带 `CancellationToken`；不要吞取消异常。
- 不要用 `async void`；不要在 `try/catch` 里 `yield`（用 `try/finally`）。
- 工具失败用 `ToolResult.Deny` 回喂模型，不向对话流抛业务异常。
- 测试不访问真实模型/数据库；用 `FakeChatModel`、`EchoAgentEngine`、内存预算/审计。真实模型验证只放在 `spikes/` 手动运行。
- 不要为了“以后可能复用”提前抽象；第二个宿主出现再抽包。

## 8. 改动流程

1. 先读 `docs/blueprint.md` 对应章节；架构变更先写 ADR。
2. 改代码 + 加测试；跑 `dotnet build` 与 `dotnet test`。
3. 更新受影响的 README / architecture / roadmap。
4. 提交信息只描述代码与业务逻辑，不写会话过程、任务流水或验证记录。
5. 不把本机路径、密钥、真实客户数据写进仓库。

## 9. 下一步优先事项

1. **P1 parity**：回放 harness 与 50 条构造 transcript 已落；下一步把 EOS 的 `LegacyTurnEngine` 和真实录制接进来，做旧/新引擎对比。
2. **P2 读路径**：100 次基线与空正文重试已落；扩到 500 次、接供应商价格、统计重试率，并补灰度回滚演练。
3. 通过 G1/G2 决策门后，再动写路径。

细节见 [docs/roadmap.md](docs/roadmap.md) 与 [docs/blueprint.md](docs/blueprint.md)。
