# P2 基线报告：读路径延迟与 Token

- 日期：2026-10-24
- 模型：`deepseek-flash`，`https://api.deepseek.com/chat/completions`
- 样本：100 次真实调用（50 文本 + 50 只读工具），另有 2 次预热不计入统计
- 运行：同一进程、串行、无并发；`TurnPipeline → MafAgentEngine`
- 工程：`spikes/Agent.Harness.Spike --baseline`
- 原始结果：`artifacts/baseline/p2-baseline-20261024-001452.json`（本地，未提交）

## 方法

- 工具选择使用 `MafAgentEngineOptions`：只读、allowlist 仅 `get_order`；`MaxEmptyResponseRetries=1`。
- 文本场景不暴露工具，prompt 为 `只回复：P2-BASELINE-OK`。
- 工具场景只暴露 `get_order`，prompt 要求查询 `SO-1001` 后回复状态。
- 文本/工具交替执行；先跑 2 次预热（文本 + 工具），不计入结果。
- `Completed` 即传输/引擎完成；另外记录精确 marker 命中、空正文与工具调用率。
- 延迟是 `TurnPipeline` 全链路（不含宿主落库/SSE 写出），单次 wall clock。
- 未配置供应商单价，因此成本为 `n/a`；可用 `--input-price-per-1m` / `--output-price-per-1m` 与 `--currency` 估算。

## 结果

| 指标 | 全部 | 文本 | 工具 |
|---|---:|---:|---:|
| 尝试 / 完成 | 100 / 100 | 50 / 50 | 50 / 50 |
| 失败 | 0 | 0 | 0 |
| p50 | 1231 ms | 749 ms | 1717 ms |
| p95 | 2081 ms | 1216 ms | 2273 ms |
| prompt tokens | 39,668 | 2,050 | 37,618 |
| completion tokens | 8,200 | 2,187 | 6,013 |
| 平均 prompt / 次 | 396.7 | 41.0 | 752.4 |
| 平均 completion / 次 | 82.0 | 43.7 | 120.3 |
| 工具调用率 | — | — | 100% |
| 精确 marker 命中 | — | 28 / 50（56%） | — |
| 空正文 | 0 | 0 | 0 |

所有 100 次 `FinishReason=stop`；没有 `length` 截断，也没有 `Failed` 事件。

## 解读

1. **工具路径仍然明显更重。** 相对文本，工具 p50 增加约 968 ms、p95 增加约 1057 ms；prompt tokens 从 41 增至 752（约 18 倍）。主要成本是工具 Schema + 工具往返。
2. **精确 marker 不适合做健康指标。** 50 次文本只有 28 次逐字命中，但 50 次全部 `Completed`；22 次是不同措辞。P2 健康检查应看“完成 + 非空 + 业务字段”，不要把固定措辞当成功率。
3. **空正文重试已进产品路径。** `MafAgentEngineOptions.MaxEmptyResponseRetries` 默认 1，只对“无正文、无工具调用、`stop`/null”重试；`length`、`tool_calls` 和异常不重试。本次 100 次没有空正文，不能据此得出重试命中率。
4. **p95 仍受长尾影响。** 100 次样本比 50 次稳定一些，但仍不足以定线上 SLO；工具 p95 2.3 s。
5. **没有旧引擎对照。** 这些是绝对基线；G2 的“延迟 ≤+10%”仍需要 EOS `LegacyTurnEngine` 的同口径数据。

## 成本

本次没有供应商价格，因此不计成本。示例（价格为占位，不是供应商报价）：

```powershell
dotnet run --project spikes\Agent.Harness.Spike\Agent.Harness.Spike.csproj -- `
  --baseline --baseline-count 100 --baseline-warmup 2 `
  --input-price-per-1m 0.5 --output-price-per-1m 2 --currency USD
```

计算口径：`prompt/1M * inputPrice + completion/1M * outputPrice`，只统计完成的样本。

## 复现

```powershell
$env:DEEPSEEK_APP_KEY = [Environment]::GetEnvironmentVariable('DEEPSEEK_APP_KEY','User')
dotnet run --project spikes\Agent.Harness.Spike\Agent.Harness.Spike.csproj -- `
  --baseline --baseline-count 100 --baseline-warmup 2
```

结果写入 `artifacts/baseline/p2-baseline-<timestamp>.json`。

## 结论与下一步

- P2 已有 100 次真实基线：文本 p50 749 / p95 1216 ms，工具 p50 1717 / p95 2273 ms；工具 prompt tokens 约为文本的 18 倍。
- “空正文”重试已实现并有单测；但重试率、成本增量还需要更大样本与供应商价格。
- 下一步：扩到 500 次、接真实单价、统计重试率；再拿 EOS 旧引擎做同口径 +10% 对比。
- 本基线不构成 G2 通过证据；G2 仍需要 EOS parity、线上抽样与回滚演练。
