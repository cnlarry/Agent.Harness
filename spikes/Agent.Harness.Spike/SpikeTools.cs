using System.Text.Json;
using Agent.Harness.Abstractions;

namespace Agent.Harness.Spike;

/// <summary>P0 用的 3 个受控工具：只读、草稿写、超大结果。</summary>
internal sealed class SpikeTools
{
    public SpikeTools()
    {
        All =
        [
            new GetOrderTool(this),
            new CreateDraftTool(this),
            new LargeNoteTool(this),
        ];
    }

    public int GetOrderCalls { get; private set; }

    public int CreateDraftCalls { get; private set; }

    public int LargeNoteCalls { get; private set; }

    public List<string> CallIds { get; } = [];

    public List<string> OrderSequence { get; } = [];

    public ITool[] All { get; }

    internal void RecordOrder(string orderNo, string callId)
    {
        GetOrderCalls++;
        OrderSequence.Add(orderNo);
        CallIds.Add(callId);
    }

    internal void RecordDraft(string callId)
    {
        CreateDraftCalls++;
        CallIds.Add(callId);
    }

    internal void RecordNote(string callId)
    {
        LargeNoteCalls++;
        CallIds.Add(callId);
    }

    private sealed class GetOrderTool(SpikeTools owner) : ITool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "get_order",
            "按订单号查询订单状态、金额与下一张订单号（只读）。",
            """
            {
              "type": "object",
              "properties": {
                "orderNo": {
                  "type": "string",
                  "description": "订单号，例如 SO-1001"
                }
              },
              "required": ["orderNo"],
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
            if (!invocation.Arguments.TryGetProperty("orderNo", out var orderNoElement) ||
                orderNoElement.ValueKind != JsonValueKind.String)
            {
                return ValueTask.FromResult(ToolResult.Deny("缺少 orderNo 参数。"));
            }

            var orderNo = orderNoElement.GetString() ?? string.Empty;
            owner.RecordOrder(orderNo, invocation.CallId);

            var (status, amount, next) = orderNo switch
            {
                "SO-1001" => ("已发货", "12800.00", (string?)"SO-1002"),
                "SO-1002" => ("待开票", "4600.00", (string?)"SO-1003"),
                "SO-1003" => ("已完成", "9900.00", null),
                "SO-2001" => ("待发货", "300.00", null),
                "SO-2002" => ("已取消", "0.00", null),
                _ => (string.Empty, string.Empty, (string?)null),
            };

            if (status.Length == 0)
            {
                return ValueTask.FromResult(ToolResult.Deny($"未找到订单 {orderNo}。"));
            }

            var content = $"订单 {orderNo}；状态={status}；金额={amount}；next={(next ?? "无")}";
            return ValueTask.FromResult(ToolResult.Success(content));
        }
    }

    private sealed class CreateDraftTool(SpikeTools owner) : ITool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "create_draft",
            "创建一张待确认的订单草稿（不落正式单据）。",
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
            ToolRisk.Draft,
            ReadOnly: false,
            AllowParallel: false);

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            if (!invocation.Arguments.TryGetProperty("title", out var titleElement) ||
                titleElement.ValueKind != JsonValueKind.String ||
                !invocation.Arguments.TryGetProperty("amount", out var amountElement) ||
                amountElement.ValueKind != JsonValueKind.Number)
            {
                return ValueTask.FromResult(ToolResult.Deny("缺少 title 或 amount 参数。"));
            }

            owner.RecordDraft(invocation.CallId);
            var draftId = $"DR-{owner.CreateDraftCalls:000}";
            var content = $"已生成草稿 {draftId}；状态=pending；标题={titleElement.GetString()}；金额={amountElement.GetDouble():0.00}";
            return ValueTask.FromResult(new ToolResult(
                Ok: true,
                ContentForModel: content,
                Draft: new { draftId, status = "pending" }));
        }
    }

    private sealed class LargeNoteTool(SpikeTools owner) : ITool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "get_large_note",
            "读取一条较长的备注（只读），用于验证大结果回喂。",
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
            ToolRisk.Read,
            ReadOnly: true,
            AllowParallel: true);

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            if (!invocation.Arguments.TryGetProperty("noteId", out var noteIdElement) ||
                noteIdElement.ValueKind != JsonValueKind.String)
            {
                return ValueTask.FromResult(ToolResult.Deny("缺少 noteId 参数。"));
            }

            owner.RecordNote(invocation.CallId);
            var noteId = noteIdElement.GetString();
            var body = string.Join(' ', Enumerable.Repeat("AgentHarness-P0-large-result.", 140));
            return ValueTask.FromResult(ToolResult.Success($"note={noteId}；{body}"));
        }
    }
}
