namespace Agent.Harness.AspNetCore;

/// <summary>当前用户不是该会话的归属者；端点应返回 403。</summary>
public sealed class ConversationAccessDeniedException(string message) : Exception(message);
