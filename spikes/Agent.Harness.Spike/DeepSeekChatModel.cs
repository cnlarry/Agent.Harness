using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Harness.Abstractions;

namespace Agent.Harness.Spike;

/// <summary>
/// P0 spike 专用的 DeepSeek OpenAI 兼容流式实现。
/// 只做协议解析；权限、会话、预算、审计仍由 Harness 负责。
/// </summary>
internal sealed class DeepSeekChatModel : IChatModel, IDisposable
{
    private readonly HttpClient _http;
    private readonly DeepSeekOptions _options;
    private readonly string? _apiKey;
    private readonly bool _ownsHttpClient;

    public DeepSeekChatModel(
        DeepSeekOptions? options = null,
        HttpClient? httpClient = null,
        string? apiKey = null)
    {
        _options = options ?? new DeepSeekOptions();
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable(_options.ApiKeyEnvironmentVariable);
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _ownsHttpClient = httpClient is null;
    }

    public string ModelName => _options.Model;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>最近一次请求体，供 Schema 断言使用；不含密钥。</summary>
    public string? LastRequestJson { get; private set; }

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                $"缺少模型密钥：环境变量 {_options.ApiKeyEnvironmentVariable} 未设置。参见 docs/secrets.md。");
        }

        var payloadJson = BuildPayload(messages, tools);

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            throw DeepSeekApiException.From(response.StatusCode, body);
        }

        using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var started = Stopwatch.StartNew();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data.Length == 0)
            {
                continue;
            }

            if (data == "[DONE]")
            {
                yield break;
            }

            ChatDelta? delta;
            using (var document = JsonDocument.Parse(data))
            {
                delta = ParseChunk(document.RootElement, (int)started.ElapsedMilliseconds);
            }

            if (delta is not null)
            {
                yield return delta;
            }
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    private string BuildPayload(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools)
    {
        var payload = new JsonObject
        {
            ["model"] = _options.Model,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["max_tokens"] = _options.MaxOutputTokens,
            ["temperature"] = _options.Temperature,
        };

        var messageArray = new JsonArray();
        foreach (var message in messages)
        {
            messageArray.Add(ToJson(message));
        }

        payload["messages"] = messageArray;

        if (tools is { Count: > 0 })
        {
            var toolArray = new JsonArray();
            foreach (var tool in tools)
            {
                var schema = JsonNode.Parse(tool.ParametersJson)
                    ?? throw new DeepSeekProtocolException($"工具 {tool.Name} 的 JSON Schema 无效。");

                toolArray.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = schema,
                    },
                });
            }

            payload["tools"] = toolArray;
        }

        var json = payload.ToJsonString();
        LastRequestJson = json;
        return json;
    }

    private static JsonObject ToJson(ChatMessage message)
    {
        var role = message.Role switch
        {
            ChatRole.System => "system",
            ChatRole.Assistant => "assistant",
            ChatRole.Tool => "tool",
            _ => "user",
        };

        var json = new JsonObject { ["role"] = role };

        if (message.Role == ChatRole.Tool)
        {
            json["tool_call_id"] = message.ToolCallId ?? string.Empty;
            json["content"] = message.Content;
            return json;
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            json["content"] = message.Content.Length == 0 ? null : message.Content;

            var toolCalls = new JsonArray();
            foreach (var call in message.ToolCalls)
            {
                toolCalls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.ArgumentsJson,
                    },
                });
            }

            json["tool_calls"] = toolCalls;
            return json;
        }

        json["content"] = message.Content;
        return json;
    }

    private static ChatDelta? ParseChunk(JsonElement root, int elapsedMs)
    {
        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var errorMessage) &&
                          errorMessage.ValueKind == JsonValueKind.String
                ? errorMessage.GetString()
                : "供应商在流内返回错误。";

            throw new DeepSeekApiException(
                HttpStatusCode.BadGateway,
                "provider_stream_error",
                "模型返回了无法处理的数据，请稍后重试。",
                providerMessage: message,
                isTransient: true);
        }

        ChatUsage? usage = ParseUsage(root, elapsedMs);

        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return usage is null ? null : new ChatDelta(ContentDelta: null, Usage: usage);
        }

        var choice = choices[0];
        string? content = null;
        string? reasoning = null;
        string? finishReason = null;
        List<ProposedToolCallFragment>? toolCallDeltas = null;

        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
        {
            content = ReadString(delta, "content");
            reasoning = ReadString(delta, "reasoning_content");

            if (delta.TryGetProperty("tool_calls", out var toolCalls) &&
                toolCalls.ValueKind == JsonValueKind.Array)
            {
                toolCallDeltas = [];
                foreach (var call in toolCalls.EnumerateArray())
                {
                    var index = call.TryGetProperty("index", out var indexElement) &&
                                indexElement.TryGetInt32(out var parsedIndex)
                        ? parsedIndex
                        : 0;

                    var id = ReadString(call, "id");
                    string? name = null;
                    string? arguments = null;

                    if (call.TryGetProperty("function", out var function) &&
                        function.ValueKind == JsonValueKind.Object)
                    {
                        name = ReadString(function, "name");
                        arguments = ReadString(function, "arguments");
                    }

                    toolCallDeltas.Add(new ProposedToolCallFragment(index, id, name, arguments));
                }
            }
        }

        finishReason = ReadString(choice, "finish_reason");

        if (content is null && reasoning is null && toolCallDeltas is null && usage is null && finishReason is null)
        {
            return null;
        }

        return new ChatDelta(
            ContentDelta: content,
            Usage: usage,
            ToolCallDeltas: toolCallDeltas,
            FinishReason: finishReason,
            ReasoningDelta: reasoning);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static ChatUsage? ParseUsage(JsonElement root, int elapsedMs)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var promptTokens = ReadInt32(usage, "prompt_tokens");
        var completionTokens = ReadInt32(usage, "completion_tokens");

        return promptTokens == 0 && completionTokens == 0
            ? null
            : new ChatUsage(promptTokens, completionTokens, elapsedMs);
    }

    private static int ReadInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.TryGetInt32(out var value)
            ? value
            : 0;
}
