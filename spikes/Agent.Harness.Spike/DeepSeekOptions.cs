namespace Agent.Harness.Spike;

/// <summary>P0 spike 的 DeepSeek 连接参数。密钥不进入这里，只记录环境变量名。</summary>
internal sealed record DeepSeekOptions(
    string Model = "deepseek-flash",
    string Endpoint = "https://api.deepseek.com/chat/completions",
    string ApiKeyEnvironmentVariable = "DEEPSEEK_APP_KEY",
    int MaxOutputTokens = 1024,
    double Temperature = 0);
