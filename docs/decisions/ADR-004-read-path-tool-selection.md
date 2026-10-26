# ADR-004：读路径灰度与工具选择默认值

- 状态：已接受（2026-10-23）
- 决策者：项目所有者

## 背景

P2 要求先灰度“只读工具”，把写工具留到 P3。此前 `MafAgentEngine` 把 `ToolRegistry` 里的所有工具都发给模型，没有引擎级开关；这导致：

- 读路径试点会意外暴露 `Draft` / `Write` / `AdminWrite` 工具；
- 宿主只能通过“别注册写工具”来规避，回滚和灰度都要改 DI 组合；
- 无法在同一个部署里用配置切换读路径工具集。

## 决策

1. 新增 `MafAgentEngineOptions`：
   - `ReadOnlyToolsOnly`：只暴露 `ReadOnly=true` 且 `Risk=Read` 的工具；
   - `EnabledTools`：工具名 allowlist；空数组表示一个都不暴露（fail-closed）；
   - `AllowConcurrentToolInvocation`：同一轮多个工具是否并发，默认 false。
2. **DI 默认安全。** `AddAgentHarnessMaf` 注册的默认 options 是 `ReadOnlyToolsOnly=true`、串行；宿主可用 configure 显式关闭。
3. **直接构造保持兼容。** `new MafAgentEngine(...)` 的 options 默认为“全部工具、串行”，避免破坏测试、spike 和已有宿主；生产接线应使用 DI。
4. **allowlist 空数组 fail-closed。** `ToolRegistry.Resolve` 的“空=全部”语义不适用于 options；引擎单独处理空 allowlist。
5. **并发默认 false。** 写工具并发会放大幂等/顺序风险；读路径默认串行，P3 再按工具 `AllowParallel` 评估。
6. **配置绑定启动时快照。** `AddAgentHarnessMaf(IConfiguration)` 从配置节绑定 options；不做运行时热切换，灰度/回滚通过重启或重新部署完成。

## 后果

- 正面：读路径灰度只需配置，不必改 DI 组合；写工具默认不可见；空 allowlist 不会意外放开全部工具；并发行为可测试。
- 负面：DI 默认只读会让“以为写了就会暴露”的写工具静默消失；宿主必须知道 `ReadOnlyToolsOnly` 并在写路径阶段显式关闭；配置切换需要重启，回滚演练必须按部署流程做。
- 边界：工具过滤不是权限边界；被暴露的只读工具仍必须按用户重新授权。过滤只减少模型可见面，不替代 `IToolPolicy`/工具内部授权。

## 考虑过的替代

| 方案 | 未选原因 |
|---|---|
| 每个环境注册不同工具集 | 无法在同一部署内灰度/回滚，配置变更要改代码 |
| 只靠 `IToolPolicy` 拒绝写工具 | 工具仍会出现在模型声明里，模型会尝试调用，产生无谓拒绝与审计噪声 |
| 端点层过滤工具 | 工具声明由引擎构建，端点拿不到注册表；会导致两套事实 |
| 默认全部工具 | 读路径试点容易误暴露写工具，违背 P2 的安全默认 |

## 复审条件

- P3 写路径需要按用户/租户动态选择工具；
- 工具数量增长到需要分组/标签而不是 allowlist；
- `AllowConcurrentToolInvocation` 在只读工具上被证明安全，需要默认开启。
