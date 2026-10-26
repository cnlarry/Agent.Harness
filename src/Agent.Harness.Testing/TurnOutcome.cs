using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Harness.Abstractions;

namespace Agent.Harness.Testing;

/// <summary>归一化的一次工具调用：跨引擎比较只认这三件事（工具名、参数、结果）。</summary>
public sealed record OutcomeToolCall(
    string Name,
    string ArgumentsJson,
    string ResultDigest,
    bool Ok);

/// <summary>归一化用量。两个引擎的估算口径不同，故只作观察项，不参与判定。</summary>
public sealed record OutcomeUsage(int PromptTokens, int CompletionTokens);

/// <summary>
/// 一次回合的归一化结果：**跨引擎对比的唯一对象**。
///
/// <para>
/// 为什么不直接比事件流：旧引擎与 MAF 引擎的循环语义本来就不同（收尾轮是否下发工具、
/// 工具是否并行、正文里的调用标记是否剥离、空回复是否重试），事件流逐条比必然全红，
/// 红得没有信息量。用户真正感知到的是「最终说了什么」与「单据上发生了什么」，
/// 这两件事就落在这里。
/// </para>
///
/// <para>
/// <see cref="RequestShapes"/> 只记每次模型调用收到的消息角色序列（如 <c>System,User</c>），
/// 用于暴露「提示词有没有送到模型」这类问题；具体正文不记、不比——两端组装方式本就不同。
/// </para>
/// </summary>
public sealed record TurnOutcome(
    string Case,
    string Engine,
    int ModelCalls,
    string FinalText,
    string? FinishReason,
    IReadOnlyList<OutcomeToolCall> ToolCalls,
    OutcomeUsage Usage,
    IReadOnlyList<string> RequestShapes,
    string? FailureCode = null,
    string? FailureMessage = null)
{
    /// <summary>引擎是否以失败收场（失败时 <see cref="FinalText"/> 通常为空）。</summary>
    [JsonIgnore]
    public bool Failed => FailureCode is not null;

    /// <summary>
    /// 从 MAF 引擎的回合事件流构造。
    ///
    /// <para>
    /// 工具调用按**事件顺序**配对：<see cref="AgentEvent.ToolStarted"/> 给名字，
    /// <see cref="AgentEvent.ToolFinished"/> 给结果摘要与成功标志，
    /// <see cref="AgentTurnResult.ToolCalls"/> 给参数。三者同序，因为引擎是
    /// 「先 Started、再 Finished、同时记一条 ToolCallRecord」逐条产出的。
    /// </para>
    /// </summary>
    public static TurnOutcome FromTurnRun(
        string caseName,
        string engine,
        TurnRun run,
        int modelCalls,
        IReadOnlyList<string>? requestShapes = null)
    {
        ArgumentNullException.ThrowIfNull(run);

        var completed = run.Completed;
        var records = completed?.Result.ToolCalls ?? [];
        var started = run.ToolStarted;

        var okByCallId = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var finished in run.ToolFinished)
        {
            okByCallId[finished.CallId] = finished.Ok;
        }

        var calls = new List<OutcomeToolCall>(started.Count);
        for (var index = 0; index < started.Count; index++)
        {
            var startedEvent = started[index];
            var record = index < records.Count ? records[index] : null;
            var ok = okByCallId.TryGetValue(startedEvent.CallId, out var finishedOk) && finishedOk;

            calls.Add(new OutcomeToolCall(
                startedEvent.Name,
                record?.ArgumentsJson ?? string.Empty,
                record?.ResultDigest ?? string.Empty,
                ok));
        }

        var failure = run.Events.OfType<AgentEvent.Failed>().LastOrDefault();

        return new TurnOutcome(
            caseName,
            engine,
            modelCalls,
            completed?.Result.Text ?? string.Empty,
            completed?.Result.FinishReason,
            calls,
            new OutcomeUsage(
                completed?.Result.Usage.PromptTokens ?? 0,
                completed?.Result.Usage.CompletionTokens ?? 0),
            requestShapes ?? [],
            failure?.Code,
            failure?.Message);
    }
}
