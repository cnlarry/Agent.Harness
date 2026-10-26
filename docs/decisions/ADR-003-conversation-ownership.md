# ADR-003：会话归属校验与 403

- 状态：已接受（2026-10-23）
- 决策者：项目所有者

## 背景

`MapAgentHarness` 的 `conversationId` 由客户端提交。`ClaimsAgentTurnFactory` 最初只校验 Claim 非空，然后按 `(TenantId, UserId, ConversationId)` 加载历史。这留下一个缺口：

- 用户 A 的会话被用户 B 猜到 `conversationId` 后，B 至少能让引擎在“空历史”下运行；
- 如果宿主的 `IConversationStore` 只按 `ConversationId` 查询，就会直接泄露 A 的历史；
- 后续写入也可能落到错误归属上。

“历史为空”不能作为授权结论。

## 决策

1. **归属检查进入 `IConversationStore`。** 新增：
   ```csharp
   ValueTask<bool> IsOwnedByAsync(
       string tenantId,
       string userId,
       string conversationId,
       CancellationToken cancellationToken = default);
   ```
   - 新会话（尚无记录）返回 `true`；
   - 已存在且属于其他用户返回 `false`；
   - 跨租户的同名会话互不影响。
2. **工厂先检查、再加载。** `ClaimsAgentTurnFactory` 在调用 `LoadAsync` 前执行 `IsOwnedByAsync`；返回 `false` 时抛 `ConversationAccessDeniedException`。
3. **端点返回 403。** `MapAgentHarness` 捕获 `ConversationAccessDeniedException`，返回 403 + JSON error；不进入 `TurnPipeline`，不产生预算/审计占用。
4. **存储层也防御。** `InMemoryConversationStore` 按 `(TenantId, ConversationId)` 记录 owner，按 `(TenantId, UserId, ConversationId)` 隔离消息；跨用户写入抛 `InvalidOperationException`，跨用户读取返回空。
5. **新增内存实现供测试/示例使用。** `Agent.Harness.Testing.InMemoryConversationStore` 覆盖归属、隔离与最近消息截断。

## 后果

- 正面：跨用户会话从“历史为空”升级为 403；端点、工厂、存储三层都有防线；测试可以端到端验证。
- 负面：这是 `IConversationStore` 的破坏性契约变更，宿主实现必须补 `IsOwnedByAsync`；首次并发创建同名会话仍存在竞态，owner 由首次写入决定，生产存储应使用唯一约束/事务。
- 剩余：会话转移、共享会话、管理员查看他人会话等场景不在本决策范围。

## 考虑过的替代

| 方案 | 未选原因 |
|---|---|
| 只返回空历史 | 不能阻止引擎运行，也不能阻止后续写入；不是授权 |
| 端点层单独查权限表 | 与存储的归属事实重复，容易出现两套事实 |
| 新增独立 `IConversationAuthorization` | 归属判断依赖存储数据，拆开后仍要回查；先保持在一个契约里 |
| 新会话一律 403 | 无法创建新会话 |

## 复审条件

- 需要共享会话、会话转移或管理员审计视图；
- 宿主存储无法高效实现 `IsOwnedByAsync`（例如跨库/跨服务）；
- 会话归属从“单用户”变成“团队/角色”模型。
