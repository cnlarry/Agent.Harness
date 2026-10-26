# Agent.Harness.Spike

P0/P2 验证工程：`Microsoft.Agents.AI` 1.25 + DeepSeek + 受控工具。

## 为什么放在仓库里

蓝图写 P0 时，Agent Harness 还准备从 EOS 抽出来，所以要求“独立目录、不要放进 EOS 仓库”。现在这个仓库本身就是 Agent Harness，再建一个同级目录会失去版本历史、评审和可复现性。

因此放在 `spikes/` 下：

- 不参与 `dotnet pack`（`IsPackable=false`）；
- 不被 `src/`、`samples/` 引用；
- 验证完成后可以整体删除，或把验证过的适配器提升到正式项目；
- 结论写在 [docs/spikes/p0-spike-report.md](../../docs/spikes/p0-spike-report.md)。

## 运行

```powershell
$env:DEEPSEEK_APP_KEY = [Environment]::GetEnvironmentVariable('DEEPSEEK_APP_KEY','User')
dotnet run --project spikes\Agent.Harness.Spike\Agent.Harness.Spike.csproj
```

只跑本地桩（不需要密钥）：

```powershell
dotnet run --project spikes\Agent.Harness.Spike\Agent.Harness.Spike.csproj -- --stub-only
```

只跑某条用例：

```powershell
dotnet run --project spikes\Agent.Harness.Spike\Agent.Harness.Spike.csproj -- --case 04
```

可选参数：

- `--model <id>`：默认 `deepseek-flash`；
- `--case 01,02`：按编号过滤；
- `--stub-only`：只跑 09（错误映射）和 10（最大轮次）；
- `--baseline`：跑 P2 读路径延迟/token 基线，不跑 P0 用例；
- `--baseline-count 100`：基线样本数（2–500，默认 100）；
- `--baseline-warmup 2`：预热样本数（默认 2，不计入统计）；
- `--input-price-per-1m` / `--output-price-per-1m`：可选，按 1M token 单价估算成本；
- `--currency USD`：成本展示币种（默认 USD）。

结果写到 `artifacts/spike/p0-results.json`，`artifacts/` 已被忽略。

## P2 基线

```powershell
$env:DEEPSEEK_APP_KEY = [Environment]::GetEnvironmentVariable('DEEPSEEK_APP_KEY','User')
dotnet run --project spikes\Agent.Harness.Spike\Agent.Harness.Spike.csproj -- `
  --baseline --baseline-count 50
```

文本与只读 `get_order` 工具交替执行，输出 p50/p95、prompt/completion token、工具调用率、空正文/精确 marker 计数；结果写到 `artifacts/baseline/p2-baseline-<timestamp>.json`。报告见 [docs/spikes/p2-baseline-report.md](../../docs/spikes/p2-baseline-report.md)。

## 用例

| # | 内容 | 依赖 |
|---|---|---|
| 01 | 单轮文本 | 真实模型 |
| 02 | 单工具调用 | 真实模型 |
| 03 | 多轮工具调用（3 轮） | 真实模型 |
| 04 | 一轮多个工具调用 | 真实模型 |
| 05 | 工具 Schema 原样保留 | 第 02 条的真实请求体 |
| 06 | `reasoning_content` 透传 | 真实模型 |
| 07 | usage 统计 | 真实模型 |
| 08 | 取消/断流 | 真实模型 |
| 09 | 401/402/429/413/5xx 映射 | 本地 HTTP 桩 |
| 10 | 最大轮次收尾 | 脚本模型 |

## 安全

- 密钥只从 `DEEPSEEK_APP_KEY` 读取，不落文件、不进日志；
- `DeepSeekChatModel.LastRequestJson` 只保存请求体（含对话与工具 Schema），不含 `Authorization`；
- 这是 spike，不是生产 provider：没有重试/退避、代理、限流、token 预算。
