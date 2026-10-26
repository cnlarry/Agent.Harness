using Agent.Harness.Abstractions;
using Agent.Harness.Testing;

namespace Agent.Harness.Tests;

public sealed record ReplayScenario(
    string Name,
    IReadOnlyList<IReadOnlyList<ChatDelta>> Turns,
    Func<ReplayTools> ToolsFactory,
    Func<IToolPolicy> PolicyFactory,
    int MaxToolRounds,
    string ExpectedText,
    string ExpectedFinishReason,
    int ExpectedToolStarts,
    int ExpectedToolCalls,
    int ExpectedToolFailures,
    int ExpectedDrafts,
    int ExpectedReasoningChars,
    int ExpectedCompleted,
    int ExpectedFailed)
{
    public override string ToString() => Name;
}

public static class ReplayScenarioFactory
{
    public static IReadOnlyList<ReplayScenario> CreateAll()
    {
        var scenarios = new List<ReplayScenario>();

        for (var index = 0; index < 12; index++)
        {
            scenarios.Add(CreateText(index));
        }

        for (var index = 0; index < 10; index++)
        {
            scenarios.Add(CreateSingleTool(index));
        }

        for (var index = 0; index < 8; index++)
        {
            scenarios.Add(CreateMultiTool(index));
        }

        for (var index = 0; index < 8; index++)
        {
            scenarios.Add(CreateMultiRound(index));
        }

        for (var index = 0; index < 4; index++)
        {
            scenarios.Add(CreateDenied(index));
        }

        for (var index = 0; index < 3; index++)
        {
            scenarios.Add(CreateDraft(index));
        }

        for (var index = 0; index < 3; index++)
        {
            scenarios.Add(CreateLarge(index));
        }

        for (var index = 0; index < 2; index++)
        {
            scenarios.Add(CreateMaxRounds(index));
        }

        return scenarios;
    }

    private static ReplayScenario CreateText(int index)
    {
        var reasoning = index % 2 == 0 ? $"reason-{index}" : null;
        var turn = new List<ChatDelta>();

        if (reasoning is not null)
        {
            turn.Add(new ChatDelta(ContentDelta: null, Usage: null, ReasoningDelta: reasoning));
        }

        turn.Add(new ChatDelta(
            ContentDelta: $"text-{index}",
            Usage: new ChatUsage(10 + index, 5 + index, 1),
            FinishReason: "stop"));

        var turns = new List<IReadOnlyList<ChatDelta>> { turn };

        return new ReplayScenario(
            $"text-{index:00}",
            turns,
            () => new ReplayTools(),
            () => new AllowAllToolPolicy(),
            MaxToolRounds: 4,
            ExpectedText: $"text-{index}",
            ExpectedFinishReason: "stop",
            ExpectedToolStarts: 0,
            ExpectedToolCalls: 0,
            ExpectedToolFailures: 0,
            ExpectedDrafts: 0,
            ExpectedReasoningChars: reasoning?.Length ?? 0,
            ExpectedCompleted: 1,
            ExpectedFailed: 0);
    }

    private static ReplayScenario CreateSingleTool(int index)
    {
        var arguments = $"{{\"text\":\"item-{index}\"}}";
        var turns = new List<IReadOnlyList<ChatDelta>>
        {
            new[] { ToolCall($"call-{index}", "echo", arguments) },
            new[] { Text($"single-{index}") },
        };

        return new ReplayScenario(
            $"single-tool-{index:00}",
            turns,
            () => new ReplayTools(),
            () => new AllowAllToolPolicy(),
            MaxToolRounds: 4,
            ExpectedText: $"single-{index}",
            ExpectedFinishReason: "stop",
            ExpectedToolStarts: 1,
            ExpectedToolCalls: 1,
            ExpectedToolFailures: 0,
            ExpectedDrafts: 0,
            ExpectedReasoningChars: 0,
            ExpectedCompleted: 1,
            ExpectedFailed: 0);
    }

    private static ReplayScenario CreateMultiTool(int index)
    {
        var count = 2 + index % 4;
        var calls = Enumerable
            .Range(0, count)
            .Select(callIndex => (
                Index: callIndex,
                Id: $"call-{index}-{callIndex}",
                Name: "echo",
                Arguments: $"{{\"text\":\"multi-{index}-{callIndex}\"}}"))
            .ToArray();

        var turns = new List<IReadOnlyList<ChatDelta>>
        {
            new[] { ToolCalls(calls) },
            new[] { Text($"multi-{index}") },
        };

        return new ReplayScenario(
            $"multi-tool-{index:00}",
            turns,
            () => new ReplayTools(),
            () => new AllowAllToolPolicy(),
            MaxToolRounds: 4,
            ExpectedText: $"multi-{index}",
            ExpectedFinishReason: "stop",
            ExpectedToolStarts: count,
            ExpectedToolCalls: count,
            ExpectedToolFailures: 0,
            ExpectedDrafts: 0,
            ExpectedReasoningChars: 0,
            ExpectedCompleted: 1,
            ExpectedFailed: 0);
    }

    private static ReplayScenario CreateMultiRound(int index)
    {
        var rounds = 2 + index % 4;
        var turns = new List<IReadOnlyList<ChatDelta>>();

        for (var round = 0; round < rounds; round++)
        {
            turns.Add(
            [
                ToolCall(
                    $"call-{index}-{round}",
                    "echo",
                    $"{{\"text\":\"round-{index}-{round}\"}}"),
            ]);
        }

        turns.Add([Text($"rounds-{index}")]);

        return new ReplayScenario(
            $"multi-round-{index:00}",
            turns,
            () => new ReplayTools(),
            () => new AllowAllToolPolicy(),
            MaxToolRounds: rounds,
            ExpectedText: $"rounds-{index}",
            ExpectedFinishReason: "stop",
            ExpectedToolStarts: rounds,
            ExpectedToolCalls: rounds,
            ExpectedToolFailures: 0,
            ExpectedDrafts: 0,
            ExpectedReasoningChars: 0,
            ExpectedCompleted: 1,
            ExpectedFailed: 0);
    }

    private static ReplayScenario CreateDenied(int index)
    {
        var turns = new List<IReadOnlyList<ChatDelta>>
        {
            new[] { ToolCall($"call-denied-{index}", "echo", """{"text":"denied"}""") },
            new[] { Text($"denied-{index}") },
        };

        Func<IToolPolicy> policy = index < 2
            ? () => new DenyAllPolicy()
            : () => new ApprovalPolicy();

        return new ReplayScenario(
            $"denied-{index:00}",
            turns,
            () => new ReplayTools(),
            policy,
            MaxToolRounds: 4,
            ExpectedText: $"denied-{index}",
            ExpectedFinishReason: "stop",
            ExpectedToolStarts: 1,
            ExpectedToolCalls: 0,
            ExpectedToolFailures: 1,
            ExpectedDrafts: 0,
            ExpectedReasoningChars: 0,
            ExpectedCompleted: 1,
            ExpectedFailed: 0);
    }

    private static ReplayScenario CreateDraft(int index)
    {
        var turns = new List<IReadOnlyList<ChatDelta>>
        {
            new[] { ToolCall($"call-draft-{index}", "draft", $"{{\"title\":\"draft-{index}\"}}") },
            new[] { Text($"drafted-{index}") },
        };

        return new ReplayScenario(
            $"draft-{index:00}",
            turns,
            () => new ReplayTools(),
            () => new AllowAllToolPolicy(),
            MaxToolRounds: 4,
            ExpectedText: $"drafted-{index}",
            ExpectedFinishReason: "stop",
            ExpectedToolStarts: 1,
            ExpectedToolCalls: 1,
            ExpectedToolFailures: 0,
            ExpectedDrafts: 1,
            ExpectedReasoningChars: 0,
            ExpectedCompleted: 1,
            ExpectedFailed: 0);
    }

    private static ReplayScenario CreateLarge(int index)
    {
        var turns = new List<IReadOnlyList<ChatDelta>>
        {
            new[] { ToolCall($"call-large-{index}", "large", $"{{\"noteId\":\"note-{index}\"}}") },
            new[] { Text($"large-{index}") },
        };

        return new ReplayScenario(
            $"large-{index:00}",
            turns,
            () => new ReplayTools(),
            () => new AllowAllToolPolicy(),
            MaxToolRounds: 4,
            ExpectedText: $"large-{index}",
            ExpectedFinishReason: "stop",
            ExpectedToolStarts: 1,
            ExpectedToolCalls: 1,
            ExpectedToolFailures: 0,
            ExpectedDrafts: 0,
            ExpectedReasoningChars: 0,
            ExpectedCompleted: 1,
            ExpectedFailed: 0);
    }

    private static ReplayScenario CreateMaxRounds(int index)
    {
        var turns = new List<IReadOnlyList<ChatDelta>>
        {
            new[] { ToolCall($"call-max-{index}-0", "echo", """{"text":"max-0"}""") },
            new[] { ToolCall($"call-max-{index}-1", "echo", """{"text":"max-1"}""") },
        };

        return new ReplayScenario(
            $"max-rounds-{index:00}",
            turns,
            () => new ReplayTools(),
            () => new AllowAllToolPolicy(),
            MaxToolRounds: 1,
            ExpectedText: string.Empty,
            ExpectedFinishReason: AgentFinishReasons.MaxToolRounds,
            ExpectedToolStarts: 2,
            ExpectedToolCalls: 1,
            ExpectedToolFailures: 1,
            ExpectedDrafts: 0,
            ExpectedReasoningChars: 0,
            ExpectedCompleted: 1,
            ExpectedFailed: 0);
    }

    private static ChatDelta Text(string text) =>
        new(
            ContentDelta: text,
            Usage: new ChatUsage(20, 10, 1),
            FinishReason: "stop");

    private static ChatDelta ToolCall(
        string id,
        string name,
        string arguments,
        int index = 0) =>
        new(
            ContentDelta: null,
            Usage: new ChatUsage(10, 5, 1),
            ToolCallDeltas:
            [
                new ProposedToolCallFragment(index, id, name, arguments),
            ],
            FinishReason: "tool_calls");

    private static ChatDelta ToolCalls(
        params (int Index, string Id, string Name, string Arguments)[] calls) =>
        new(
            ContentDelta: null,
            Usage: new ChatUsage(20, 10, 1),
            ToolCallDeltas: calls
                .Select(call => new ProposedToolCallFragment(
                    call.Index,
                    call.Id,
                    call.Name,
                    call.Arguments))
                .ToArray(),
            FinishReason: "tool_calls");
}

public sealed class ReplayTools
{
    public EchoTool Echo { get; } = new();

    public DraftTool Draft { get; } = new();

    public LargeTool Large { get; } = new();

    public ITool[] All => [Echo, Draft, Large];

    public int Calls => Echo.Calls + Draft.Calls + Large.Calls;

    public sealed class EchoTool : ITool
    {
        public int Calls { get; private set; }

        public ToolDescriptor Descriptor { get; } = new(
            "echo",
            "返回输入文本（只读）。",
            """
            {
              "type": "object",
              "properties": {
                "text": { "type": "string" }
              },
              "required": ["text"],
              "additionalProperties": false
            }
            """,
            ToolRisk.Read,
            ReadOnly: true,
            AllowParallel: true);

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var text = invocation.Arguments.TryGetProperty("text", out var value)
                ? value.GetString()
                : null;
            return ValueTask.FromResult(ToolResult.Success($"echo:{text}"));
        }
    }

    public sealed class DraftTool : ITool
    {
        public int Calls { get; private set; }

        public ToolDescriptor Descriptor { get; } = new(
            "draft",
            "创建一个待确认草稿。",
            """
            {
              "type": "object",
              "properties": {
                "title": { "type": "string" }
              },
              "required": ["title"],
              "additionalProperties": false
            }
            """,
            ToolRisk.Draft,
            ReadOnly: false);

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new ToolResult(
                Ok: true,
                ContentForModel: "draft-ok",
                Draft: new { draftId = $"D{Calls}" }));
        }
    }

    public sealed class LargeTool : ITool
    {
        public int Calls { get; private set; }

        public ToolDescriptor Descriptor { get; } = new(
            "large",
            "返回一条较长备注（只读）。",
            """
            {
              "type": "object",
              "properties": {
                "noteId": { "type": "string" }
              },
              "required": ["noteId"],
              "additionalProperties": false
            }
            """,
            ToolRisk.Read,
            ReadOnly: true,
            AllowParallel: true);

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var content = "large:" + new string('x', 2048);
            return ValueTask.FromResult(ToolResult.Success(content));
        }
    }
}

internal sealed class DenyAllPolicy : IToolPolicy
{
    public ValueTask<ToolDecision> EvaluateAsync(
        ToolDescriptor tool,
        ToolInvocation invocation,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<ToolDecision>(new ToolDecision.Deny("forbidden", "no access"));
}

internal sealed class ApprovalPolicy : IToolPolicy
{
    public ValueTask<ToolDecision> EvaluateAsync(
        ToolDescriptor tool,
        ToolInvocation invocation,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<ToolDecision>(
            new ToolDecision.RequireApproval("""{"card":"confirm"}"""));
}
