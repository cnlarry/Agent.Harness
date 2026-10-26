# ADR-001：名称、命名空间与范围

- 状态：已接受（2026-10-23）
- 决策者：项目所有者

## 背景

EOS 的可行性分析原以 `AgentKit` 为代号。项目所有者决定改用 **Agent Harness**，.NET 命名空间为 `Agent.Harness`，仓库根目录名保留空格（`Agent Harness`）。

“Agent Harness”与 Microsoft Agent Framework 的 `HarnessAgent` / “Agent Harness”概念重名；后者是自带 Shell、文件访问、Web 搜索、Todo 的开箱即用长任务 Agent。

## 决策

1. 项目名：**Agent Harness**。
2. 命名空间/包：`Agent.Harness.*`（`Agent.Harness.Abstractions`、`.Core`、`.Maf`、`.AspNetCore`、`.Testing`）。
3. 范围：企业管理系统里的**受控 Agent 治理壳**——工具白名单、策略、预算、审计、会话、事件；不实现通用多 Agent 框架。
4. 不采用 MAF 的 HarnessAgent、Hosting、Durable、A2A、Foundry Hosted。
5. 与 MAF 的边界：MAF 只出现在 `Agent.Harness.Maf`，且只用核心 Agent/工具/工作流。

## 后果

- 正面：名称直观表达“给 Agent 套约束”；与 EOS 的“工作助手治理”经验一致；命名空间短。
- 负面：与 MAF HarnessAgent 重名，需要 README、包描述、ADR 持续区分；搜索“Agent Harness”会混入 MAF 文档。
- 缓解：README 首节显式说明“不是 MAF Harness”；包描述带 “governed agent runtime for line-of-business systems”；内部沟通用 `Agent.Harness.*` 指代代码。

## 考虑过的替代

| 名称 | 未选原因 |
|---|---|
| `AgentKit` | 原代号；所有者更偏好 Harness 的“约束”语义 |
| `AgentRuntime` | 与 EOS 已删除的 `EOS.AgentRuntime` 混淆 |
| `AgentGovernance` | 只表达治理，不表达“运行时/装具” |
| `Harness` | 太泛，且与测试 harness 混淆 |

## 复审条件

- 与 MAF 生态产生实际命名冲突（例如包名/命名空间冲突）；
- 项目从“个人/内部骨架”转为公开发布，需要品牌区分；
- 范围从“治理壳”扩张为通用 Agent 平台。
