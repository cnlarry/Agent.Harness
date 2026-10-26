using Agent.Harness.Abstractions;
using Agent.Harness.Core;
using Agent.Harness.Maf;
using Agent.Harness.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class ToolSchemaSnapshotTests
{
    [Fact]
    public async Task ChatModelChatClient_SendsEachSchemaByteForByte()
    {
        var cases = new (string Name, string Schema, ToolRisk Risk)[]
        {
            (
                "search_orders",
                """
                {
                  "type": "object",
                  "properties": {
                    "keyword": {
                      "type": "string",
                      "description": "客户名或单号关键字"
                    }
                  },
                  "required": ["keyword"],
                  "additionalProperties": false
                }
                """,
                ToolRisk.Read),
            (
                "create_draft",
                """
                {
                  "type": "object",
                  "properties": {
                    "title": { "type": "string", "description": "草稿标题" },
                    "amount": { "type": "number", "description": "金额" }
                  },
                  "required": ["title", "amount"],
                  "additionalProperties": false
                }
                """,
                ToolRisk.Draft),
            (
                "get_large_note",
                """
                {
                  "type": "object",
                  "properties": {
                    "noteId": { "type": "string", "description": "备注编号" }
                  },
                  "required": ["noteId"],
                  "additionalProperties": false
                }
                """,
                ToolRisk.Read),
        };

        foreach (var item in cases)
        {
            var tool = new SnapshotTool(item.Name, item.Schema, item.Risk);
            var recorder = new RecordingChatModel(FakeChatModel.Text("ok"));
            var engine = new MafAgentEngine(
                recorder,
                new ToolRegistry([tool]),
                new AllowAllToolPolicy(),
                NullLoggerFactory.Instance);

            var request = new AgentTurnRequest(
                TenantId: "tenant",
                UserId: "user",
                ConversationId: "conversation",
                Input: "hi",
                History: [],
                SystemPrompt: "system",
                CorrelationId: "correlation");

            await TurnRun.CollectAsync(engine, request);

            var transcript = recorder.Build(item.Name);
            var call = Assert.Single(transcript.Calls);
            var definition = Assert.Single(call.Tools!);

            Assert.Equal(item.Name, definition.Name);
            Assert.Equal(item.Schema, definition.ParametersJson);

            var function = new JsonSchemaAIFunction(
                tool.Descriptor,
                (_, _) => ValueTask.FromResult(ToolResult.Success("ok")));
            Assert.Equal(item.Schema, function.JsonSchema.GetRawText());
        }
    }

    private sealed class SnapshotTool(string name, string schema, ToolRisk risk) : ITool
    {
        public ToolDescriptor Descriptor { get; } = new(
            name,
            $"{name} description",
            schema,
            risk);

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ToolResult.Success("ok"));
    }
}
