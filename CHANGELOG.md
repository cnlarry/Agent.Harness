# Changelog

本文件记录各版本的**能力变化**。版本号即 `Directory.Build.props` 的 `<Version>`，与 git 标签 `vX.Y.Z` 一致。

## 0.1.0 — 2026-10-26

首个公开版本：受控 Agent 运行时的骨架 + 引擎适配 + 跨引擎 parity 工具。

### 骨架与契约

- `Agent.Harness.Abstractions`：回合、事件、工具、治理、会话、模型契约；不依赖任何 Agent 框架。
- `Agent.Harness.Core`：`ToolRegistry`（白名单、风险分级、按名解析）、`TurnPipeline`（预算预留 → 引擎执行 → 结算 + 审计）。
- `Agent.Harness.Maf`：`MafAgentEngine`、`ChatModelChatClient`（`IChatModel` → `IChatClient` 防腐层）、`JsonSchemaAIFunction`（工具 JSON Schema 原样保留）。
- `Agent.Harness.AspNetCore`：`MapAgentHarness` SSE 端点、Claims 身份组装、会话归属校验。
- `Agent.Harness.Testing`：`FakeChatModel`、内存预算/审计、`EchoAgentEngine`、`InMemoryConversationStore`、录制与回放。
- `samples/Agent.Harness.Sample.Console`：无模型依赖的回显示例。

### 引擎与适配

- 同一次模型调用的流式 update 共享 `MessageId`；一轮多个 tool call 合并为一条 update；多 tool result 按 `tool_call_id` 拆成多条 `role=tool` 消息；`FinishReason` 透传。
- 达到最大工具轮次时补齐未执行的 `ToolFinished(Ok=false)`，并把结束原因标为 `max_tool_rounds`；`ToolResult.Ok` / `Draft` 透传。
- `ChatOptions.Instructions` 映射为首条 `system` 消息：不做这层映射，宿主的提示词不会到达模型，且没有任何报错。
- `MafAgentEngineOptions`：只读工具过滤、工具 allowlist（空数组 fail-closed）、并发开关（默认串行）、空正文重试（默认 1 次）、收尾轮说明（`FinalRoundInstruction`，默认不追加）。
- 收尾轮说明装饰器位于重试装饰器**外层**：一次重试属于同一次模型调用，不能被算成新一轮。
- `AddAgentHarnessMaf` 支持从 `IConfiguration` 绑定灰度开关。

### 治理与端点

- `ChatModelException` + `TurnPipeline`：引擎异常转成稳定的 `AgentEvent.Failed`，取消仍向上抛。
- `MapAgentHarness` 只接受 `Input` / `ConversationId` / `PageContext`；租户、用户、提示词、轮次上限全部来自 Claims 与服务端选项。
- `IConversationStore.IsOwnedByAsync`：跨用户读取会话返回 403，不靠"历史为空"。

### 回放与守卫

- `RecordingChatModel` / `ReplayChatModel` / `TurnRun` / `TurnRunComparer`：在 `IChatModel` 边界录制与回放，回放严格校验每次调用的消息与工具 Schema。
- 50 条构造 transcript 的 record → replay 对比：文本、单/多工具、多轮、拒绝、草稿、大结果、最大轮次。
- 工具 Schema 逐字快照：模型侧收到的 `ParametersJson` 与 `ToolDescriptor` 完全一致。
- 越权/泄露用例：策略拒绝不执行工具、工具按请求身份重新授权、拒绝结果不含敏感值、非 `ChatModelException` 不泄露原消息。
- SSE 失败端到端：引擎异常 → `event: error`，无 `event: done`，预算与审计按未完成结算。

### 跨引擎 parity

- `TurnOutcome` + `TurnOutcomeComparer`：把一次回合归一化成"最终正文 + 工具调用序列 + 结果 + 结束原因"，按 **Fail / Info / Excluded** 三档判定，每条差异带理由。
- `ParityCase` 用例格式（模型脚本 + 工具桩 + 回合输入），格式即跨仓契约。
- `SequenceChatModel`：按序号回放、不校验请求，用于跨引擎对比（单引擎回归仍用 `ReplayChatModel`）。
- `tools/Agent.Harness.Parity`：`run` 跑新引擎、`compare` 出报告；两仓只靠 JSON 文件对齐，不做项目引用。

### 文档

`README.md`、`AGENTS.md`、`docs/architecture.md`、`docs/blueprint.md`、`docs/roadmap.md`、`docs/secrets.md`、`docs/decisions/ADR-001`–`ADR-005`、`docs/spikes/`（P0 报告、P2 基线报告）。

### 已知未完成

- **真实 transcript 的进程内录制**：目前靠宿主侧录制（EOS 的 `AssistantTranscriptRecorder`）与库内会话重建，
  本库尚未内置；`docs/roadmap.md` P1 记录了缺口。
- **读路径灰度切流量**：只读开关与影子运行已通，切流量与回滚演练未做。
- **写路径（P3）、抽包与第二宿主（P4）、MAF Workflows（P5）**：未开始。
- `MafAgentEngine` **未达生产可用**：缺 parity/灰度证据前，不要宣传成可直接上生产。
