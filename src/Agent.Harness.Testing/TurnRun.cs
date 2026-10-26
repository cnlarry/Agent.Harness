using Agent.Harness.Abstractions;

namespace Agent.Harness.Testing;

/// <summary>收集一个回合的完整事件流，供回放对比与测试断言使用。</summary>
public sealed class TurnRun
{
    public List<AgentEvent> Events { get; } = [];

    public AgentEvent.Completed? Completed => Events.OfType<AgentEvent.Completed>().LastOrDefault();

    public string Text => Completed?.Result.Text ?? string.Empty;

    public AgentUsage Usage => Completed?.Result.Usage ?? AgentUsage.Empty;

    public IReadOnlyList<AgentEvent.TextDelta> TextDeltas =>
        Events.OfType<AgentEvent.TextDelta>().ToArray();

    public IReadOnlyList<AgentEvent.ReasoningDelta> ReasoningDeltas =>
        Events.OfType<AgentEvent.ReasoningDelta>().ToArray();

    public IReadOnlyList<AgentEvent.ToolStarted> ToolStarted =>
        Events.OfType<AgentEvent.ToolStarted>().ToArray();

    public IReadOnlyList<AgentEvent.ToolFinished> ToolFinished =>
        Events.OfType<AgentEvent.ToolFinished>().ToArray();

    public static async Task<TurnRun> CollectAsync(
        IAgentEngine engine,
        AgentTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);

        var run = new TurnRun();
        await foreach (var evt in engine
            .StreamAsync(request, cancellationToken)
            .ConfigureAwait(false))
        {
            run.Events.Add(evt);
        }

        return run;
    }
}

/// <summary>按事件顺序比较两个回合；返回空列表表示等价。</summary>
public static class TurnRunComparer
{
    public static IReadOnlyList<string> Compare(TurnRun expected, TurnRun actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var differences = new List<string>();

        if (expected.Events.Count != actual.Events.Count)
        {
            differences.Add(
                $"事件数不同：期望 {expected.Events.Count}，实际 {actual.Events.Count}。");
        }

        var count = Math.Min(expected.Events.Count, actual.Events.Count);
        for (var index = 0; index < count; index++)
        {
            CompareEvent(expected.Events[index], actual.Events[index], index, differences);
        }

        return differences;
    }

    private static void CompareEvent(
        AgentEvent expected,
        AgentEvent actual,
        int index,
        List<string> differences)
    {
        if (expected.GetType() != actual.GetType())
        {
            differences.Add(
                $"事件 {index} 类型不同：期望 {expected.GetType().Name}，实际 {actual.GetType().Name}。");
            return;
        }

        switch (expected)
        {
            case AgentEvent.TextDelta text:
                CompareText(text.Text, ((AgentEvent.TextDelta)actual).Text, index, "TextDelta", differences);
                break;

            case AgentEvent.ReasoningDelta reasoning:
                CompareText(
                    reasoning.Text,
                    ((AgentEvent.ReasoningDelta)actual).Text,
                    index,
                    "ReasoningDelta",
                    differences);
                break;

            case AgentEvent.ToolStarted started:
                var actualStarted = (AgentEvent.ToolStarted)actual;
                if (started.CallId != actualStarted.CallId ||
                    started.Name != actualStarted.Name ||
                    started.Risk != actualStarted.Risk)
                {
                    differences.Add($"事件 {index} ToolStarted 不一致。");
                }

                break;

            case AgentEvent.ToolFinished finished:
                var actualFinished = (AgentEvent.ToolFinished)actual;
                if (finished.CallId != actualFinished.CallId ||
                    finished.Name != actualFinished.Name ||
                    finished.Digest != actualFinished.Digest ||
                    finished.Ok != actualFinished.Ok)
                {
                    differences.Add($"事件 {index} ToolFinished 不一致。");
                }

                break;

            case AgentEvent.Completed completed:
                CompareResult(completed.Result, ((AgentEvent.Completed)actual).Result, index, differences);
                break;

            case AgentEvent.Failed failed:
                var actualFailed = (AgentEvent.Failed)actual;
                if (failed.Code != actualFailed.Code || failed.Message != actualFailed.Message)
                {
                    differences.Add($"事件 {index} Failed 不一致。");
                }

                break;
        }
    }

    private static void CompareResult(
        AgentTurnResult expected,
        AgentTurnResult actual,
        int index,
        List<string> differences)
    {
        CompareText(expected.Text, actual.Text, index, "Completed.Text", differences);

        if (expected.ModelName != actual.ModelName)
        {
            differences.Add($"事件 {index} ModelName 不同：{expected.ModelName} vs {actual.ModelName}。");
        }

        if (expected.FinishReason != actual.FinishReason)
        {
            differences.Add(
                $"事件 {index} FinishReason 不同：{expected.FinishReason ?? "<null>"} vs {actual.FinishReason ?? "<null>"}。");
        }

        if (expected.Usage != actual.Usage)
        {
            differences.Add($"事件 {index} Usage 不同。");
        }

        if (expected.ToolCalls.Count != actual.ToolCalls.Count)
        {
            differences.Add(
                $"事件 {index} ToolCalls 数不同：{expected.ToolCalls.Count} vs {actual.ToolCalls.Count}。");
        }
        else
        {
            for (var callIndex = 0; callIndex < expected.ToolCalls.Count; callIndex++)
            {
                var expectedCall = expected.ToolCalls[callIndex];
                var actualCall = actual.ToolCalls[callIndex];

                if (expectedCall.Name != actualCall.Name ||
                    expectedCall.ResultDigest != actualCall.ResultDigest ||
                    !JsonEqual(expectedCall.ArgumentsJson, actualCall.ArgumentsJson))
                {
                    differences.Add($"事件 {index} ToolCall {callIndex} 不一致。");
                }
            }
        }

        if (expected.Drafts.Count != actual.Drafts.Count)
        {
            differences.Add(
                $"事件 {index} Drafts 数不同：{expected.Drafts.Count} vs {actual.Drafts.Count}。");
        }
    }

    private static void CompareText(
        string expected,
        string actual,
        int index,
        string label,
        List<string> differences)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            differences.Add($"事件 {index} {label} 不同。");
        }
    }

    private static bool JsonEqual(string? expected, string? actual)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        try
        {
            return System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(expected),
                System.Text.Json.Nodes.JsonNode.Parse(actual));
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
