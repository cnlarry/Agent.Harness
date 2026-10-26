using System.Net;
using System.Text.Json;
using Agent.Harness.Abstractions;

namespace Agent.Harness.Spike;

/// <summary>
/// DeepSeek HTTP 错误映射。用户可见消息只带状态码、稳定错误码和安全文案；
/// 供应商原始文案留在 <see cref="ProviderMessage"/>，密钥和请求头不进入异常。
/// </summary>
internal sealed class DeepSeekApiException : ChatModelException
{
    public DeepSeekApiException(
        HttpStatusCode statusCode,
        string code,
        string message,
        string? providerMessage = null,
        bool isTransient = false)
        : base(code, message, isTransient)
    {
        StatusCode = statusCode;
        ProviderMessage = providerMessage;
    }

    public HttpStatusCode StatusCode { get; }

    public string? ProviderMessage { get; }

    public static DeepSeekApiException From(HttpStatusCode statusCode, string body)
    {
        var providerMessage = TryReadProviderMessage(body);
        var code = statusCode switch
        {
            HttpStatusCode.Unauthorized => "auth_failed",
            HttpStatusCode.PaymentRequired => "insufficient_balance",
            HttpStatusCode.TooManyRequests => "rate_limited",
            HttpStatusCode.RequestEntityTooLarge => "payload_too_large",
            _ when (int)statusCode >= 500 => "provider_error",
            _ => $"http_{(int)statusCode}",
        };

        var (safeMessage, isTransient) = statusCode switch
        {
            HttpStatusCode.Unauthorized => ("模型认证失败，请检查密钥配置。", false),
            HttpStatusCode.PaymentRequired => ("模型账户余额不足。", false),
            HttpStatusCode.TooManyRequests => ("模型请求过于频繁，请稍后重试。", true),
            HttpStatusCode.RequestEntityTooLarge => ("请求内容过大。", false),
            _ when (int)statusCode >= 500 => ("模型服务暂时不可用，请稍后重试。", true),
            _ => ("模型请求失败。", false),
        };

        var message = $"DeepSeek 请求失败（HTTP {(int)statusCode}，{code}）：{safeMessage}";
        return new DeepSeekApiException(statusCode, code, message, providerMessage, isTransient);
    }

    private static string? TryReadProviderMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return Sanitize(message.GetString());
            }
        }
        catch (JsonException)
        {
            // 非 JSON 错误体按纯文本处理。
        }

        return Sanitize(body);
    }

    private static string? Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var singleLine = text.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= 200 ? singleLine : singleLine[..200];
    }
}
