using System.Text.Json;
using Agent.Harness.Abstractions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Maf;

/// <summary>
/// 用宿主现有的 JSON Schema 构造 MAF <see cref="AIFunction"/>。
/// 不走反射生成，保证工具声明与既有产品契约逐字一致。
/// </summary>
public sealed class JsonSchemaAIFunction : AIFunction
{
    private readonly ToolDescriptor _descriptor;
    private readonly Func<JsonElement, CancellationToken, ValueTask<ToolResult>> _invoke;
    private readonly JsonElement _schema;

    public JsonSchemaAIFunction(
        ToolDescriptor descriptor,
        Func<JsonElement, CancellationToken, ValueTask<ToolResult>> invoke)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(invoke);

        _descriptor = descriptor;
        _invoke = invoke;

        using var document = JsonDocument.Parse(descriptor.ParametersJson);
        _schema = document.RootElement.Clone();
    }

    public override string Name => _descriptor.Name;

    public override string Description => _descriptor.Description;

    public override JsonElement JsonSchema => _schema;

    // AIFunction.InvokeAsync 非 virtual；自定义实现必须覆盖 InvokeCoreAsync。
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var payload = arguments
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        var json = JsonSerializer.SerializeToElement(payload);
        var result = await _invoke(json, cancellationToken).ConfigureAwait(false);
        return result.ContentForModel;
    }
}
