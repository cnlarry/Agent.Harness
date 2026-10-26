namespace Agent.Harness.Abstractions;

/// <summary>模型侧消息角色；数值与 EOS 既有落库枚举对齐。</summary>
public enum ChatRole
{
    System = 3,
    User = 1,
    Assistant = 2,
    Tool = 4,
}

/// <summary>进入模型的一条消息。</summary>
public sealed record ChatMessage(
    ChatRole Role,
    string Content,
    IReadOnlyList<CompletedToolCall>? ToolCalls = null,
    string? ToolCallId = null);

/// <summary>发给模型的工具声明。JSON Schema 由服务端硬编码。</summary>
public sealed record ToolDefinition(string Name, string Description, string ParametersJson);

/// <summary>单次模型调用的用量。</summary>
public sealed record ChatUsage(int PromptTokens, int CompletionTokens, int ElapsedMs);

/// <summary>流中的工具调用分片。</summary>
public sealed record ProposedToolCallFragment(
    int Index,
    string? Id,
    string? Name,
    string? ArgumentsFragment);

/// <summary>模型流式增量。</summary>
public sealed record ChatDelta(
    string? ContentDelta,
    ChatUsage? Usage,
    IReadOnlyList<ProposedToolCallFragment>? ToolCallDeltas = null,
    string? FinishReason = null,
    string? ReasoningDelta = null);

/// <summary>
/// 供应商无关的最小模型接口。实现只负责出网与协议解析，
/// 不做权限判断、不落库、不组装提示词、不执行工具。
/// </summary>
public interface IChatModel
{
    string ModelName { get; }

    bool IsConfigured { get; }

    IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        CancellationToken cancellationToken = default);
}

/// <summary>当前模型档案（供应商、端点、窗口、能力）。密钥不在此处。</summary>
public sealed record ModelProfile(
    string ProviderCode,
    string Model,
    string BaseUrl,
    string ApiKeyEnvironmentVariable,
    int ContextWindow,
    int MaxOutputTokens = 4096,
    bool SupportsTools = true);

public interface IModelProfileProvider
{
    ValueTask<ModelProfile> GetCurrentAsync(CancellationToken cancellationToken = default);
}
