namespace Agent.Harness.Abstractions;

/// <summary>
/// 模型调用的稳定失败。实现必须提供已脱敏、可直接展示给用户的消息；
/// 供应商原始文案只允许留在诊断字段或日志里。
/// </summary>
public class ChatModelException : Exception
{
    public ChatModelException(
        string code,
        string message,
        bool isTransient = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        IsTransient = isTransient;
    }

    /// <summary>稳定错误码，例如 <c>rate_limited</c>、<c>auth_failed</c>。</summary>
    public string Code { get; }

    /// <summary>是否属于可重试的暂时性失败。</summary>
    public bool IsTransient { get; }
}
