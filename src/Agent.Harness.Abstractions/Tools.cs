using System.Text.Json;

namespace Agent.Harness.Abstractions;

/// <summary>工具风险分级。策略层与并发控制都以它为依据。</summary>
public enum ToolRisk
{
    Read = 0,
    Draft = 1,
    Write = 2,
    AdminWrite = 3,
}

/// <summary>工具的稳定声明。JSON Schema 由宿主持有，不接受客户端传入。</summary>
public sealed record ToolDescriptor(
    string Name,
    string Description,
    string ParametersJson,
    ToolRisk Risk,
    bool ReadOnly = true,
    bool AllowParallel = false);

/// <summary>一次工具执行的服务端上下文。</summary>
public sealed record ToolInvocation(
    string TenantId,
    string UserId,
    string ConversationId,
    string CallId,
    JsonElement Arguments,
    AgentPageContext? PageContext = null);

/// <summary>工具执行结果。失败以 <see cref="Ok"/>=false 回喂模型，不向对话流抛业务异常。</summary>
public sealed record ToolResult(bool Ok, string ContentForModel, object? Draft = null)
{
    public static ToolResult Success(string content) => new(true, content);

    public static ToolResult Deny(string reason) => new(false, reason);
}

/// <summary>落库/展示用的工具调用摘要。</summary>
public sealed record ToolCallRecord(string Name, string ArgumentsJson, string ResultDigest);

/// <summary>一个受控工具。实现内部必须按当前用户重新授权。</summary>
public interface ITool
{
    ToolDescriptor Descriptor { get; }

    ValueTask<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default);
}
