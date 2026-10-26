using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agent.Harness.Testing;

/// <summary>差异档位。<see cref="Fail"/> 判为回归；<see cref="Info"/> 只记录，由人判读。</summary>
public enum ParitySeverity
{
    /// <summary>真回归：用户可见结果或单据上的事实不同。</summary>
    Fail,

    /// <summary>已知差异或观察项：记录下来，不阻断。</summary>
    Info,

    /// <summary>明确不比：原因写在 <see cref="ParityDifference.Detail"/> 里。</summary>
    Excluded,
}

/// <summary>一条对比结论。</summary>
public sealed record ParityDifference(ParitySeverity Severity, string Code, string Detail);

/// <summary>
/// 旧引擎（EOS <c>ChatService</c>）与新引擎（<c>MafAgentEngine</c>）的归一化结果对比。
///
/// <para>
/// 判定口径分三档，理由写在每一档的代码注释里。**不是**「逐字段相等才算通过」：
/// 两个引擎的循环语义存在已知差异（收尾轮、并行、摘要截断长度），把它们判成回归
/// 只会让人关掉报告。
/// </para>
/// </summary>
public static class TurnOutcomeComparer
{
    /// <summary>
    /// 摘要截断长度的已知差异：旧引擎取 <c>CHAT_TOOL_DIGEST_LENGTH</c>（默认 160），
    /// 新引擎固定 200。故摘要按「短的是不是长的前缀」判等，长度差单独记一条观察项。
    /// </summary>
    private static readonly string[] NotCompared =
    [
        "请求消息正文：两端提示词组装方式不同（旧引擎自带 system 消息，MAF 走 ChatOptions.Instructions），只比角色序列。",
        "事件粒度与顺序：旧引擎并行执行只读工具，完成顺序不定；MAF 默认串行。工具集合与结果仍逐条比。",
        "耗时与时间戳：不是行为，不参与对比。",
        "Draft（结构化草稿）：只落库/展示，不进模型上下文；本版不比对。",
    ];

    public static IReadOnlyList<ParityDifference> Compare(TurnOutcome legacy, TurnOutcome maf)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(maf);

        var differences = new List<ParityDifference>();

        if (!string.Equals(legacy.Case, maf.Case, StringComparison.Ordinal))
        {
            differences.Add(new ParityDifference(
                ParitySeverity.Fail,
                "case_mismatch",
                $"用例名不同：legacy={legacy.Case} / maf={maf.Case}"));
        }

        CompareFailure(legacy, maf, differences);
        CompareFinalText(legacy, maf, differences);
        CompareToolCalls(legacy, maf, differences);
        CompareRequestShapes(legacy, maf, differences);
        CompareObservations(legacy, maf, differences);

        foreach (var reason in NotCompared)
        {
            differences.Add(new ParityDifference(ParitySeverity.Excluded, "not_compared", reason));
        }

        return differences;
    }

    /// <summary>
    /// 成败必须一致：一边给出回答、另一边报错，是用户能立刻感知的差异。
    /// 失败**码**不比——两端码表各自独立（旧引擎 <c>AI_MODEL_EMPTY_REPLY</c> 对
    /// 新引擎 <c>agent_error</c>），比码只会得到永远不收敛的噪音。
    /// </summary>
    private static void CompareFailure(
        TurnOutcome legacy,
        TurnOutcome maf,
        List<ParityDifference> differences)
    {
        if (legacy.Failed == maf.Failed)
        {
            if (legacy.Failed)
            {
                differences.Add(new ParityDifference(
                    ParitySeverity.Info,
                    "failure_code",
                    $"两端均失败，码不同属正常：legacy={legacy.FailureCode} / maf={maf.FailureCode}"));
            }

            return;
        }

        differences.Add(new ParityDifference(
            ParitySeverity.Fail,
            "failure_state",
            legacy.Failed
                ? $"旧引擎失败（{legacy.FailureCode}），新引擎成功"
                : $"旧引擎成功，新引擎失败（{maf.FailureCode}：{maf.FailureMessage}）"));
    }

    private static void CompareFinalText(
        TurnOutcome legacy,
        TurnOutcome maf,
        List<ParityDifference> differences)
    {
        if (string.Equals(legacy.FinalText, maf.FinalText, StringComparison.Ordinal))
        {
            return;
        }

        differences.Add(new ParityDifference(
            ParitySeverity.Fail,
            "final_text",
            $"最终正文不同：legacy=「{Preview(legacy.FinalText)}」 / maf=「{Preview(maf.FinalText)}」"));
    }

    private static void CompareToolCalls(
        TurnOutcome legacy,
        TurnOutcome maf,
        List<ParityDifference> differences)
    {
        if (legacy.ToolCalls.Count != maf.ToolCalls.Count)
        {
            differences.Add(new ParityDifference(
                ParitySeverity.Fail,
                "tool_call_count",
                $"工具调用数不同：legacy={legacy.ToolCalls.Count} / maf={maf.ToolCalls.Count}"
                + $"（legacy=[{string.Join(',', legacy.ToolCalls.Select(call => call.Name))}]"
                + $" maf=[{string.Join(',', maf.ToolCalls.Select(call => call.Name))}]）"));
            return;
        }

        for (var index = 0; index < legacy.ToolCalls.Count; index++)
        {
            var expected = legacy.ToolCalls[index];
            var actual = maf.ToolCalls[index];

            if (!string.Equals(expected.Name, actual.Name, StringComparison.Ordinal))
            {
                differences.Add(new ParityDifference(
                    ParitySeverity.Fail,
                    "tool_name",
                    $"第 {index} 次工具调用名不同：legacy={expected.Name} / maf={actual.Name}"));
                continue;
            }

            if (!JsonEqual(expected.ArgumentsJson, actual.ArgumentsJson))
            {
                differences.Add(new ParityDifference(
                    ParitySeverity.Fail,
                    "tool_arguments",
                    $"第 {index} 次工具调用（{expected.Name}）参数不同："
                    + $"legacy={Preview(expected.ArgumentsJson)} / maf={Preview(actual.ArgumentsJson)}"));
            }

            if (expected.Ok != actual.Ok)
            {
                differences.Add(new ParityDifference(
                    ParitySeverity.Fail,
                    "tool_ok",
                    $"第 {index} 次工具调用（{expected.Name}）成功标志不同："
                    + $"legacy={expected.Ok} / maf={actual.Ok}"));
            }

            if (!DigestMatches(expected.ResultDigest, actual.ResultDigest))
            {
                differences.Add(new ParityDifference(
                    ParitySeverity.Fail,
                    "tool_digest",
                    $"第 {index} 次工具调用（{expected.Name}）结果摘要不同："
                    + $"legacy=「{Preview(expected.ResultDigest)}」 / maf=「{Preview(actual.ResultDigest)}」"));
            }
            else if (!string.Equals(expected.ResultDigest, actual.ResultDigest, StringComparison.Ordinal))
            {
                differences.Add(new ParityDifference(
                    ParitySeverity.Info,
                    "tool_digest_length",
                    $"第 {index} 次工具调用（{expected.Name}）摘要只差截断长度："
                    + $"legacy={expected.ResultDigest.Length} / maf={actual.ResultDigest.Length} 字符"));
            }
        }
    }

    /// <summary>
    /// 请求形状分三件事比，**档位不同**：
    ///
    /// <list type="bullet">
    /// <item>每条请求的**首条消息角色** → FAIL。首条不是 System 意味着宿主的提示词根本没送到模型，
    /// 而模型会照常回答、只是不再守规矩，从外部看不出来。</item>
    /// <item>完整角色序列 → INFO。旧引擎在收尾轮追加一条 System（<c>FinalRoundInstruction</c>），
    /// 是它自己的循环语义。</item>
    /// <item>每轮工具声明数 → INFO。工具面不同会改变模型可选的动作，但两套循环各自都有
    /// 收尾轮语义（实测 MAF 的 <c>FunctionInvokingChatClient</c> 也会在最后一轮撤掉工具），
    /// 故先按观察项记录，真出现分歧再单独判。</item>
    /// </list>
    /// </summary>
    private static void CompareRequestShapes(
        TurnOutcome legacy,
        TurnOutcome maf,
        List<ParityDifference> differences)
    {
        var legacyHeads = legacy.RequestShapes.Select(FirstRoleOf).ToArray();
        var mafHeads = maf.RequestShapes.Select(FirstRoleOf).ToArray();

        if (!legacyHeads.SequenceEqual(mafHeads, StringComparer.Ordinal))
        {
            differences.Add(new ParityDifference(
                ParitySeverity.Fail,
                "request_head_role",
                $"每条请求的首条消息角色不同：legacy=[{string.Join(" | ", legacyHeads)}]"
                + $" maf=[{string.Join(" | ", mafHeads)}]"
                + "（首条不是 System 说明提示词没送到模型）"));
        }

        var legacyRoles = legacy.RequestShapes.Select(RolesOf).ToArray();
        var mafRoles = maf.RequestShapes.Select(RolesOf).ToArray();

        if (!legacyRoles.SequenceEqual(mafRoles, StringComparer.Ordinal))
        {
            differences.Add(new ParityDifference(
                ParitySeverity.Info,
                "request_roles",
                $"完整角色序列不同：legacy=[{string.Join(" | ", legacyRoles)}]"
                + $" maf=[{string.Join(" | ", mafRoles)}]"
                + "（旧引擎收尾轮会追加一条 System 说明「不能再调工具」）"));
        }

        var legacyTools = legacy.RequestShapes.Select(ToolsOf).ToArray();
        var mafTools = maf.RequestShapes.Select(ToolsOf).ToArray();

        if (!legacyTools.SequenceEqual(mafTools, StringComparer.Ordinal))
        {
            differences.Add(new ParityDifference(
                ParitySeverity.Info,
                "request_tool_surface",
                $"每轮下发的工具数不同：legacy=[{string.Join(" | ", legacyTools)}]"
                + $" maf=[{string.Join(" | ", mafTools)}]"
                + "（工具面不同会改变模型可选的动作，但两套循环各自都有收尾轮语义，先按观察项记录）"));
        }
    }

    /// <summary>取首条消息的角色：<c>System,User tools=3</c> → <c>System</c>。</summary>
    private static string FirstRoleOf(string shape)
    {
        var roles = RolesOf(shape);
        var comma = roles.IndexOf(',');
        return comma < 0 ? roles : roles[..comma];
    }

    /// <summary>把 <c>System,User tools=3</c> 拆成角色序列 <c>System,User</c>。</summary>
    private static string RolesOf(string shape)
    {
        var index = shape.IndexOf(" tools=", StringComparison.Ordinal);
        return index < 0 ? shape : shape[..index];
    }

    /// <summary>把 <c>System,User tools=3</c> 拆成工具数 <c>3</c>（未下发为 <c>none</c>）。</summary>
    private static string ToolsOf(string shape)
    {
        var index = shape.IndexOf(" tools=", StringComparison.Ordinal);
        return index < 0 ? "unknown" : shape[(index + " tools=".Length)..];
    }

    private static void CompareObservations(
        TurnOutcome legacy,
        TurnOutcome maf,
        List<ParityDifference> differences)
    {
        if (legacy.ModelCalls != maf.ModelCalls)
        {
            differences.Add(new ParityDifference(
                ParitySeverity.Info,
                "model_calls",
                $"模型调用次数不同：legacy={legacy.ModelCalls} / maf={maf.ModelCalls}"
                + "（旧引擎在工具轮上限处会多跑一轮纯文本收尾，属已知语义差异）"));
        }

        if (!string.Equals(legacy.FinishReason, maf.FinishReason, StringComparison.Ordinal))
        {
            differences.Add(new ParityDifference(
                ParitySeverity.Info,
                "finish_reason",
                $"结束原因不同：legacy={legacy.FinishReason ?? "<null>"} / maf={maf.FinishReason ?? "<null>"}"));
        }

        if (legacy.Usage != maf.Usage)
        {
            differences.Add(new ParityDifference(
                ParitySeverity.Info,
                "usage",
                $"用量不同：legacy={legacy.Usage.PromptTokens}/{legacy.Usage.CompletionTokens}"
                + $" maf={maf.Usage.PromptTokens}/{maf.Usage.CompletionTokens}"
                + "（旧引擎在模型未返回用量时按字符估算，新引擎记 0）"));
        }
    }

    /// <summary>摘要判等：完全相同，或短的那个是长的前缀（截断长度不同所致）。</summary>
    private static bool DigestMatches(string expected, string actual)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return true;
        }

        if (expected.Length == 0 || actual.Length == 0)
        {
            return false;
        }

        return expected.Length < actual.Length
            ? actual.StartsWith(expected, StringComparison.Ordinal)
            : expected.StartsWith(actual, StringComparison.Ordinal);
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
            return JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Preview(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "<空>";
        }

        var single = text.ReplaceLineEndings(" ");
        return single.Length <= 80 ? single : single[..80] + "…";
    }
}
