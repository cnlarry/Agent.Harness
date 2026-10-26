using Agent.Harness.Abstractions;
using Agent.Harness.Core;
using Agent.Harness.Testing;

var budget = new InMemoryTurnBudget();
var audit = new InMemoryAuditSink();
var pipeline = new TurnPipeline(new EchoAgentEngine(), budget, audit);

var request = new AgentTurnRequest(
    TenantId: "demo",
    UserId: "demo-user",
    ConversationId: "demo-conversation",
    Input: "你好，Agent Harness",
    History: [],
    SystemPrompt: "你是 Agent Harness 的示例助手。",
    CorrelationId: Guid.NewGuid().ToString("N"));

await foreach (var evt in pipeline.StreamAsync(request))
{
    Console.WriteLine(evt switch
    {
        AgentEvent.TextDelta delta => $"delta: {delta.Text}",
        AgentEvent.Completed completed =>
            $"done: {completed.Result.Text} ({completed.Result.Usage.TotalTokens} tokens)",
        AgentEvent.Failed failed => $"error: {failed.Code} {failed.Message}",
        _ => evt.GetType().Name,
    });
}
