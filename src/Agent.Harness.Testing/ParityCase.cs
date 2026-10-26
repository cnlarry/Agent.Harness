using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Harness.Abstractions;

namespace Agent.Harness.Testing;

/// <summary>
/// 用例里的工具桩：声明（名/描述/Schema/风险）与执行结果都由用例固定。
///
/// <para>
/// 声明必须**逐字**取自真实宿主录到的工具声明，否则回放时模型看到的工具面与生产不同，
/// 对比就失去意义。
/// </para>
/// </summary>
public sealed record ParityToolStub(
    string Name,
    string Description,
    string ParametersJson,
    ToolRisk Risk = ToolRisk.Read,
    bool ReadOnly = true,
    string ResultContent = "",
    bool Ok = true);

/// <summary>一次模型调用的脚本化输出（与 <see cref="ChatCallTranscript"/> 的 Output 同构）。</summary>
public sealed record ParityModelCall(IReadOnlyList<ChatDelta> Output);

/// <summary>
/// 一条跨引擎对比用例：模型脚本 + 工具桩 + 回合输入。
///
/// <para>
/// **不录请求**（消息正文与工具声明都不进用例）。原因：两个引擎组装的请求本来就不同
/// （旧引擎自带 system 消息，MAF 走 <c>ChatOptions.Instructions</c>），录进来也没法逐字比。
/// 请求只在跑的时候按角色序列记一笔，见 <see cref="TurnOutcome.RequestShapes"/>。
/// </para>
/// </summary>
public sealed record ParityCase(
    string Name,
    string ModelName,
    string SystemPrompt,
    string UserInput,
    int MaxToolRounds,
    IReadOnlyList<ParityToolStub> Tools,
    IReadOnlyList<ParityModelCall> Calls,
    string? Note = null,
    string? FinalRoundInstruction = null);

/// <summary>用例与结果的 JSON 读写。格式即跨仓契约：字段名改动等于破坏兼容。</summary>
public static class ParityCaseFile
{
    /// <summary>统一序列化口径：camelCase、枚举按字符串、缩进（用例要能直接读、能进 diff）。</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 中文不转 \uXXXX：用例与报告要能直接读、能进 diff。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static ParityCase Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var caseItem = JsonSerializer.Deserialize<ParityCase>(File.ReadAllText(path), Json);
        return caseItem ?? throw new InvalidOperationException($"用例无法解析：{path}");
    }

    /// <summary>按文件名排序加载目录下全部 <c>*.json</c>；目录不存在时返回空。</summary>
    public static IReadOnlyList<ParityCase> LoadAll(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(Load)
            .ToArray();
    }

    public static void Save<T>(string path, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    }

    public static T LoadOutcome<T>(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        return value ?? throw new InvalidOperationException($"结果文件无法解析：{path}");
    }
}
