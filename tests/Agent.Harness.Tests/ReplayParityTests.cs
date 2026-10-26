using System.Runtime.CompilerServices;
using Agent.Harness.Abstractions;
using Agent.Harness.Core;
using Agent.Harness.Maf;
using Agent.Harness.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class ReplayParityTests
{
    public static IEnumerable<object[]> Scenarios() =>
        ReplayScenarioFactory.CreateAll().Select(scenario => new object[] { scenario });

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task RecordThenReplay_ProducesEquivalentTurn(ReplayScenario scenario)
    {
        var request = new AgentTurnRequest(
            TenantId: "tenant",
            UserId: "user",
            ConversationId: "conversation",
            Input: "hello",
            History: [],
            SystemPrompt: "system",
            CorrelationId: Guid.NewGuid().ToString("N"),
            MaxToolRounds: scenario.MaxToolRounds);

        var recorder = new RecordingChatModel(new ScriptedSequenceModel(scenario.Turns));
        var recordedTools = scenario.ToolsFactory();
        var recorded = await TurnRun.CollectAsync(
            CreateEngine(recorder, recordedTools, scenario.PolicyFactory()),
            request);

        var transcript = recorder.Build(scenario.Name);

        var replayModel = new ReplayChatModel(transcript);
        var replayTools = scenario.ToolsFactory();
        var replayed = await TurnRun.CollectAsync(
            CreateEngine(replayModel, replayTools, scenario.PolicyFactory()),
            request);

        replayModel.EnsureCompleted();

        var differences = TurnRunComparer.Compare(recorded, replayed);
        Assert.Empty(differences);

        Assert.Equal(scenario.ExpectedText, replayed.Text);
        Assert.Equal(scenario.ExpectedFinishReason, replayed.Completed?.Result.FinishReason);
        Assert.Equal(scenario.ExpectedToolStarts, replayed.ToolStarted.Count);
        Assert.Equal(scenario.ExpectedToolCalls, replayTools.Calls);
        Assert.Equal(scenario.ExpectedToolFailures, replayed.ToolFinished.Count(result => !result.Ok));
        Assert.Equal(scenario.ExpectedDrafts, replayed.Completed?.Result.Drafts.Count ?? 0);
        Assert.Equal(
            scenario.ExpectedReasoningChars,
            replayed.ReasoningDeltas.Sum(delta => delta.Text.Length));
        Assert.Equal(scenario.ExpectedCompleted, replayed.Completed is null ? 0 : 1);
        Assert.Equal(scenario.ExpectedFailed, replayed.Events.OfType<AgentEvent.Failed>().Count());

        var expectedPromptTokens = scenario.Turns.Sum(
            turn => turn.Sum(delta => delta.Usage?.PromptTokens ?? 0));
        var expectedCompletionTokens = scenario.Turns.Sum(
            turn => turn.Sum(delta => delta.Usage?.CompletionTokens ?? 0));

        Assert.Equal(expectedPromptTokens, replayed.Usage.PromptTokens);
        Assert.Equal(expectedCompletionTokens, replayed.Usage.CompletionTokens);
    }

    private static MafAgentEngine CreateEngine(
        IChatModel model,
        ReplayTools tools,
        IToolPolicy policy) =>
        new(model, new ToolRegistry(tools.All), policy, NullLoggerFactory.Instance);

    private sealed class ScriptedSequenceModel(
        IReadOnlyList<IReadOnlyList<ChatDelta>> turns) : IChatModel
    {
        private int _index;

        public string ModelName => "scripted-sequence";

        public bool IsConfigured => true;

        public async IAsyncEnumerable<ChatDelta> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_index >= turns.Count)
            {
                throw new InvalidOperationException(
                    $"脚本模型只准备了 {turns.Count} 轮，引擎请求了第 {_index + 1} 轮。");
            }

            var turn = turns[_index++];
            foreach (var delta in turn)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return delta;
            }
        }
    }
}
