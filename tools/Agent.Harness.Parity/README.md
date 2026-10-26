# Agent.Harness.Parity

引擎 parity 工具：把**同一批脚本化模型输出**分别喂给两套引擎，再对比两边归一化后的结果。

```text
旧引擎（EOS ChatService，在 EOS 仓跑）  ─┐
                                        ├─ 归一化结果 JSON ─→ compare ─→ 报告
新引擎（MafAgentEngine，在本仓跑）      ─┘
```

## 为什么不是"逐事件相等"

两套引擎的循环语义本来就不同（收尾轮要不要额外说明、只读工具是否并行、正文里的调用标记是否剥离、
空回复是否重试），事件流逐条比必然全红，红得没有信息量。用户真正感知到的是
**最终说了什么**与**单据上发生了什么**，这两件事落在归一化结果里（`TurnOutcome`）。

## 判定三档

| 档 | 含义 | 例子 |
|---|---|---|
| `Fail` | 真回归：用户可见结果或单据事实不同 | 最终正文不同、工具调用序列不同、**提示词没送到模型** |
| `Info` | 已知语义差异或观察项 | 模型调用次数、结束原因、用量估算口径、工具面 |
| `Excluded` | 明确不比，理由随行打印 | 请求消息正文、事件粒度、耗时、Draft |

判据写在 `src/Agent.Harness.Testing/TurnOutcomeComparer.cs`，每条都带理由。

## 收尾轮说明（`FinalRoundInstruction`）

达到工具轮上限的那一轮，旧引擎会追加一条 system 说明（"本轮是最后一步，不能再调工具"）并撤掉工具声明，
否则模型会把"还想再查一次"写成正文里的调用标记，整轮回答作废。

新引擎侧由 `MafAgentEngineOptions.FinalRoundInstruction` 提供**同一段文本**（默认 null = 不追加，措辞属提示词、
由宿主持有）。用例用 `finalRoundInstruction` 字段把它带进来；EOS 侧测试会断言它与
`ChatService.FinalRoundInstruction` 逐字相同，避免"拿旧措辞比新行为"。

## 跑法

```powershell
# 1) 旧引擎侧（在 EOS 仓）：回放用例并写出 legacy 结果
cd <EOS>
dotnet test EOS.API.Tests\EOS.API.Tests.csproj --filter "FullyQualifiedName~LegacyParityTests"

# 2) 新引擎侧（本仓）：回放同一批用例
dotnet run --project tools\Agent.Harness.Parity -- run `
    --cases <EOS>\EOS.API.Tests\parity\cases `
    --out artifacts\parity\maf

# 3) 对比并出报告（有 Fail 时退出码 1）
dotnet run --project tools\Agent.Harness.Parity -- compare `
    --cases <EOS>\EOS.API.Tests\parity\cases `
    --legacy <EOS>\artifacts\parity\legacy `
    --maf artifacts\parity\maf `
    --report <EOS>\artifacts\parity\report.md
```

## 用例归谁

- **格式**归本仓（`ParityCase` / `TurnOutcome` / `TurnOutcomeComparer`）——它是跨仓契约。
- **真实用例**归宿主仓（EOS 的 `EOS.API.Tests/parity-cases/`）——录制源在那里，
  只有宿主能回放自己的引擎。
- `samples/` 只放**自检用**的合成用例（虚构工具），与宿主用例无关，避免同一份内容两仓各存一份而漂移。

两仓**不做项目引用**，只靠 JSON 文件对齐：各自的 `clone` 都能独立构建。改动字段名等于破坏契约。

## 用例里为什么不录请求

旧引擎自带 system 消息、新引擎走 `ChatOptions.Instructions`，两端组装的请求本来就不同，
录进来也没法逐字比。请求只在跑的时候按**角色序列 + 本轮工具数**记一笔
（`System,User tools=3`），用来暴露"提示词有没有送到模型""最后一轮还发不发工具"这类问题。

## 已知边界

- **一轮多工具已支持**：旧引擎并行执行只读工具时完成顺序不定，故成功标志按 `(工具名, 结果摘要)` 配对，
  不按位置配对（位置配在并行下会随机错位，报出来的是假回归）。
- 用例可以是**合成的**，也可以由宿主从**真实会话**重建（EOS 侧 `SessionParityCaseGenerator` 只读
  `ASSISTANT_MESSAGE` 重建）。两者都只验证循环语义，**不衡量回答质量**——模型输出是被回放固定的。
- 真实会话重建有三处损失（系统提示词未落库、流式分片丢失、轮次边界丢失），写在用例的 `note` 里。
