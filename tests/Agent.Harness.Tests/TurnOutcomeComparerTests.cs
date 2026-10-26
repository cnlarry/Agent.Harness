using Agent.Harness.Testing;
using Xunit;

namespace Agent.Harness.Tests;

/// <summary>归一化结果对比的判定口径：哪些算回归、哪些只记录。</summary>
public sealed class TurnOutcomeComparerTests
{
    private static TurnOutcome Outcome(
        string text = "找到客户订单。",
        int modelCalls = 2,
        string? finishReason = null,
        OutcomeToolCall[]? calls = null,
        OutcomeUsage? usage = null,
        string[]? requestShapes = null,
        string? failureCode = null) =>
        new(
            "case-a",
            "engine",
            modelCalls,
            text,
            finishReason,
            calls ?? [new OutcomeToolCall("search_records", """{"keyword":"DD2608"}""", "module=1606 total=1", true)],
            usage ?? new OutcomeUsage(50, 10),
            requestShapes ?? ["System,User tools=1", "System,User,Assistant,Tool tools=1"],
            failureCode);

    private static IReadOnlyList<ParityDifference> Fails(IReadOnlyList<ParityDifference> differences) =>
        [.. differences.Where(difference => difference.Severity == ParitySeverity.Fail)];

    [Fact]
    public void 完全一致时没有回归()
    {
        var differences = TurnOutcomeComparer.Compare(Outcome(), Outcome());

        Assert.Empty(Fails(differences));
    }

    [Fact]
    public void 最终正文不同判为回归()
    {
        var differences = TurnOutcomeComparer.Compare(Outcome(), Outcome(text: "没有找到。"));

        var failure = Assert.Single(Fails(differences));
        Assert.Equal("final_text", failure.Code);
    }

    [Fact]
    public void 工具参数只差空白不算回归()
    {
        var spaced = new OutcomeToolCall("search_records", """{ "keyword" : "DD2608" }""", "module=1606 total=1", true);

        var differences = TurnOutcomeComparer.Compare(Outcome(), Outcome(calls: [spaced]));

        Assert.Empty(Fails(differences));
    }

    [Fact]
    public void 工具调用数不同判为回归()
    {
        var differences = TurnOutcomeComparer.Compare(Outcome(), Outcome(calls: []));

        var failure = Assert.Single(Fails(differences));
        Assert.Equal("tool_call_count", failure.Code);
    }

    [Fact]
    public void 工具成功标志不同判为回归()
    {
        var denied = new OutcomeToolCall("search_records", """{"keyword":"DD2608"}""", "module=1606 total=1", false);

        var differences = TurnOutcomeComparer.Compare(Outcome(), Outcome(calls: [denied]));

        var failure = Assert.Single(Fails(differences));
        Assert.Equal("tool_ok", failure.Code);
    }

    [Fact]
    public void 摘要只差截断长度时记观察项不判回归()
    {
        // 旧引擎摘要截断到 160 字符，新引擎 200：内容相同、长度不同属已知差异。
        var longText = new string('x', 200);
        var legacy = new OutcomeToolCall("search_records", "{}", longText[..160], true);
        var maf = new OutcomeToolCall("search_records", "{}", longText, true);

        var differences = TurnOutcomeComparer.Compare(Outcome(calls: [legacy]), Outcome(calls: [maf]));

        Assert.Empty(Fails(differences));
        Assert.Contains(differences, difference => difference.Code == "tool_digest_length");
    }

    [Fact]
    public void 摘要内容不同判为回归()
    {
        var other = new OutcomeToolCall("search_records", """{"keyword":"DD2608"}""", "module=9999 total=0", true);

        var differences = TurnOutcomeComparer.Compare(Outcome(), Outcome(calls: [other]));

        var failure = Assert.Single(Fails(differences));
        Assert.Equal("tool_digest", failure.Code);
    }

    [Fact]
    public void 模型调用次数不同只记观察项()
    {
        var differences = TurnOutcomeComparer.Compare(Outcome(modelCalls: 2), Outcome(modelCalls: 3));

        Assert.Empty(Fails(differences));
        Assert.Contains(differences, difference => difference.Code == "model_calls");
    }

    [Fact]
    public void 一边失败一边成功判为回归()
    {
        var differences = TurnOutcomeComparer.Compare(
            Outcome(),
            Outcome(text: string.Empty, calls: [], failureCode: "agent_error"));

        // 失败必然连带正文与工具调用一起不同，这里只钉住"成败状态"这一条必须报出来。
        Assert.Contains(Fails(differences), failure => failure.Code == "failure_state");
    }

    [Fact]
    public void 提示词没送到模型时判为回归()
    {
        // MAF 走 ChatOptions.Instructions；适配层若没把它转成 system 消息，这里就会显形。
        var differences = TurnOutcomeComparer.Compare(
            Outcome(),
            Outcome(requestShapes: ["User tools=1", "User,Assistant,Tool tools=1"]));

        var failure = Assert.Single(Fails(differences));
        Assert.Equal("request_head_role", failure.Code);
    }

    [Fact]
    public void 收尾轮追加说明与撤掉工具只记观察项()
    {
        // 旧引擎在收尾轮追加 FinalRoundInstruction（一条 System）并撤掉工具声明；
        // MAF 两样都不做。这是两套循环的已知语义差异，不是缺陷。
        var legacy = Outcome(requestShapes:
            ["System,User tools=1", "System,User,Assistant,Tool tools=1", "System,User,Assistant,Tool,System tools=none"]);
        var maf = Outcome(requestShapes:
            ["System,User tools=1", "System,User,Assistant,Tool tools=1", "System,User,Assistant,Tool tools=1"]);

        var differences = TurnOutcomeComparer.Compare(legacy, maf);

        Assert.Empty(Fails(differences));
        Assert.Equal(
            ParitySeverity.Info,
            Assert.Single(differences, difference => difference.Code == "request_roles").Severity);
        Assert.Equal(
            ParitySeverity.Info,
            Assert.Single(differences, difference => difference.Code == "request_tool_surface").Severity);
    }

    [Fact]
    public void 报告里逐条列出不比的事项()
    {
        var differences = TurnOutcomeComparer.Compare(Outcome(), Outcome());

        Assert.Equal(4, differences.Count(difference => difference.Severity == ParitySeverity.Excluded));
    }
}
