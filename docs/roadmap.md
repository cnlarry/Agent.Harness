# 路线图

> 详细论证、风险与验收见 [blueprint.md](blueprint.md)。本文只做阶段与决策门。

## 阶段

| 阶段 | 内容 | 退出条件 | 状态 |
|---|---|---|---|
| **P0** | 独立 spike：MAF 1.25 + DeepSeek + 3 工具 + 多轮/收尾/usage/取消/Schema | 10/10 用例通过 | ✅ 10/10（2026-10-23；[报告](spikes/p0-spike-report.md)） |
| **P1** | 契约稳定 + `IChatModel` 边界录制回放 + parity 基线 | 50 轮回放 100%；现有测试全绿 | 🟡 回放基线 50/50（构造 transcript）；**EOS 旧引擎对比已落**：合成 2 条 + 从库内真实会话重建 10 条，全部 PASS（只剩用量估算口径 1 条观察项）；进程内流式录制未做 |
| **P2** | MAF 读路径灰度（只读工具） | 回放 parity 100%；线上抽样 ≥99%；延迟 ≤+10% | 🟡 只读开关/配置绑定/100 次基线（文本 p50 749、工具 1717、整体 p95 2081 ms）/空正文重试已落；**EOS 影子运行已跑通**（真实 31 工具 → 只读 23；写意图降级不误执行）；切流量待"EOS 如何引用 Harness"定案（需私有 feed 或 submodule） |
| **P3** | 写路径 + 默认切换 + 回滚 | 0 数据错误/0 幂等冲突；成本 ≤+5%；2 周无 P1/P2 | ⬜ 未开始 |
| **P4** | 抽包 + 第二宿主 | 第二宿主 <150 行接入；包可独立构建 | ⬜ 未开始 |
| **P5** | MAF Workflows 试点 | 端到端跑通；人工确认可审计；不引入新基础设施 | ⬜ 未开始 |

## 决策门

| 门 | 时点 | 通过条件 | 结果 | 不通过 |
|---|---|---|---|---|
| G0 | P0 结束 | 10/10；Schema/推理/收尾无阻断 | ✅ 有条件通过：Failed/最大轮次/入口已补；parity 待 P1 | 停止迁移，保留自研 |
| G1 | P1 结束 | 50 轮回放 100%；测试全绿 | 🟡 基线 50/50；旧/新引擎对比已跑（合成 2 + 真实重建 10，全 PASS）；进程内流式录制未做 | 不进入 P2 |
| G2 | P2 结束 | 读路径 parity 100%；线上 ≥99%；延迟 ≤+10% | 🟡 开关/allowlist/100 次基线/空正文重试已备；parity 13/13 PASS、影子运行已通；线上抽样与回滚演练未做 | 延长灰度/修适配 |
| G3 | P3 结束 | 写路径 0 数据错误；成本 ≤+5%；2 周无 P1/P2 | ⬜ 未开始 | 回滚 |
| G4 | P4 结束 | 第二宿主 <150 行接入；包可独立构建 | ⬜ 未开始 | 不抽包 |

## 当前优先事项

1. **P1 parity**：旧引擎侧（EOS `ChatService`）与新引擎侧的对比工具已落（`tools/Agent.Harness.Parity`，
   归一化结果 + 三档判定，用例在宿主仓：合成 2 条入库、真实会话重建 10 条落 `artifacts/`）；下一步做**进程内流式录制**
   接进来（当前 2 条是构造用例），并扩到覆盖多工具并行、写路径与失败路径。
2. 读路径灰度：部署级开关已备；补回滚演练、线上抽样与延迟监控。
3. 扩到 500 次基线、接供应商价格、统计空正文重试率。

已落：`AgentEvent.Failed`、`FinishReason=max_tool_rounds`、`MapAgentHarness` 的 Claims 组装与 `TurnPipeline` 接线、`RecordingChatModel`/`ReplayChatModel`/`TurnRunComparer` 与 50 条回放、工具 Schema 逐字快照、工具拒绝/重新授权/泄露用例、SSE 失败端到端、适配器 reasoning/usage/取消/异常契约、`IConversationStore.IsOwnedByAsync` 会话归属与 403、`MafAgentEngineOptions` 只读 allowlist/DI 默认只读/并发默认 false/IConfiguration 绑定、P2 100 次延迟/token 基线与空正文重试。P0 工程位于 `spikes/Agent.Harness.Spike`；蓝图中的同级目录写法已按当前仓库调整。

## 暂不做

- Hosting / Durable / A2A / Foundry Hosted；
- MAF Harness（Shell/文件/Web 搜索）；
- 多 Agent 编排；
- 持久化 `AgentSession`；
- 没有第二个宿主前的多包拆分。
