# 密钥配置与安全管理

> 适用于模型 API key（当前计划接入 DeepSeek）。核心原则：**密钥只存在于环境变量或密钥库，绝不进入仓库、数据库、日志或对话记录。**

## 1. 读取约定

- `ModelProfile.ApiKeyEnvironmentVariable` 声明变量名，例如 `DEEPSEEK_APP_KEY`。
- 代码通过 `Environment.GetEnvironmentVariable(name)` 读取；缺失时 fail-fast，错误信息只包含变量名。
- 不把密钥写进 `appsettings*.json`、数据库、审计、异常消息或客户端响应。
- 当前骨架尚未实现 DeepSeek 供应商；新增 `IChatModel` 实现时必须遵守本节。

```csharp
var keyName = profile.ApiKeyEnvironmentVariable;
var apiKey = Environment.GetEnvironmentVariable(keyName);
if (string.IsNullOrWhiteSpace(apiKey))
{
    throw new InvalidOperationException(
        $"缺少模型密钥：环境变量 {keyName} 未设置。参见 docs/secrets.md。");
}
```

> 异常和日志里只写变量名，不写值。不要为了“方便排查”把 key 的前后几位打进日志。

## 2. 本地开发（Windows）

### 推荐：用户级环境变量

用系统对话框设置，key 不会进入任何 shell 历史：

1. `Win + R` → `sysdm.cpl` → “高级” → “环境变量”；
2. 在“用户变量”新建 `DEEPSEEK_APP_KEY`，值填 `sk-...`；
3. 确定后重启终端、Visual Studio / Rider、DSH 等进程。

或者用 PowerShell 安全提示输入（同样不会把明文写进命令历史）：

```powershell
$secure = Read-Host -AsSecureString 'DeepSeek API key'
$plain  = [System.Net.NetworkCredential]::new('', $secure).Password
[Environment]::SetEnvironmentVariable('DEEPSEEK_APP_KEY', $plain, 'User')
Remove-Variable secure, plain
```

设置后新开的进程才能读到。如果 IDE 仍读不到，注销重登或重启 Explorer，让环境块刷新。

验证时只显示“是否存在”和长度，不打印明文：

```powershell
if ([string]::IsNullOrWhiteSpace($env:DEEPSEEK_APP_KEY)) {
    'DEEPSEEK_APP_KEY: missing'
} else {
    "DEEPSEEK_APP_KEY: set (length=$($env:DEEPSEEK_APP_KEY.Length))"
}
```

### 临时会话

只对当前终端及其子进程生效，关闭终端即失效：

```powershell
$env:DEEPSEEK_APP_KEY = Read-Host -AsSecureString 'DeepSeek API key' |
    ConvertFrom-SecureString -AsPlainText
```

不要把 `sk-...` 直接敲进命令行——PowerShell 会把它记录到 PSReadLine 历史文件。

### 关于 `dotnet user-secrets`

ASP.NET Core 宿主可以用 `dotnet user-secrets` 把密钥存在仓库外（`%APPDATA%\Microsoft\UserSecrets\`）。但当前契约统一按环境变量名读取；在宿主明确采用配置系统之前，不要同时维护两套密钥来源。

## 3. CI

GitHub Actions 使用仓库或 Environment secret，再映射为环境变量：

```yaml
env:
  DEEPSEEK_APP_KEY: ${{ secrets.DEEPSEEK_APP_KEY }}
```

- 不要在 workflow 里 `echo` 密钥；GitHub 的掩码不是绝对保险。
- 不要让来自 fork 的未信任 PR 拿到 secret。
- Azure DevOps 用 secret variable group，显式映射到任务环境变量。

## 4. 生产

- 用密钥库（Azure Key Vault、HashiCorp Vault、AWS Secrets Manager 等）或操作系统保护的存储，在进程启动时注入环境变量。
- 每个环境（开发/预发/生产）使用独立密钥；只授予运行服务的身份，不给交互用户。
- 在供应商侧设置额度/告警，避免密钥泄漏后被刷爆。
- 轮换流程：新建密钥 → 部署验证 → 吊销旧密钥；审计只记录“轮换了哪个变量”，不记录值。
- IIS 多应用共用一台机器时，避免使用机器级环境变量；优先用密钥库 + 应用级注入。

## 5. 仓库防线

`.gitignore` 已覆盖 `.env`、`.env.*`、`*.local.json` 和常见证书/私钥文件。仍然要遵守：

- 不提交任何含密钥的 `appsettings*.json`；开发机用环境变量。
- 提交前可自查：

```powershell
git grep -nE "sk-[A-Za-z0-9_-]{20,}"
```

- 可选：接入 `gitleaks` 或 GitHub secret scanning 作为 pre-commit / CI 检查。
- **一旦泄漏：先吊销并轮换，再清理历史。** 只删掉某次提交不够，历史里仍然可读；必要时用 `git filter-repo` 重写并强推。

## 6. 日志与错误

- 不记录 `Authorization` 请求头、原始 HTTP 请求体或完整配置对象。
- `ModelProfile` 只存变量名和端点，不存密钥本身。
- 工具/审计事件记录 `TenantId` / `UserId` / `ConversationId` / `CorrelationId`，不记录密钥。
- 模型错误映射为稳定错误码（401/402/429/5xx），不要把供应商响应里的敏感内容原样回给前端。

## 7. 开工前检查清单

- [ ] 当前进程能读到 `DEEPSEEK_APP_KEY`（用长度检查，不打印值）
- [ ] `git check-ignore -v .env` 确认被忽略
- [ ] `git grep` 搜不到 `sk-` 开头的真实密钥
- [ ] 密钥缺失时启动 fail-fast，错误信息只含变量名
- [ ] 日志、审计、异常中不含密钥
