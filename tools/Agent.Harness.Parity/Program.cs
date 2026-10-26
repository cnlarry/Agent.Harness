using System.Text;
using Agent.Harness.Abstractions;
using Agent.Harness.Core;
using Agent.Harness.Maf;
using Agent.Harness.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent.Harness.Parity;

/// <summary>
/// 引擎 parity 工具：把同一批脚本化模型输出分别喂给旧引擎（EOS <c>ChatService</c>，在 EOS 仓跑）
/// 与新引擎（<see cref="MafAgentEngine"/>，在这里跑），再对比两边的归一化结果。
///
/// <para>
/// 两个命令：<c>run</c> 跑新引擎出结果，<c>compare</c> 比对并出报告。
/// 用例与结果都是 JSON 文件，**跨仓只靠文件对齐**——两边都能独立 clone 与构建，
/// 不需要跨仓项目引用。
/// </para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var options = ParseOptions(args.Skip(1).ToArray());

        return args[0].ToLowerInvariant() switch
        {
            "run" => await RunAsync(options),
            "compare" => Compare(options),
            _ => Unknown(args[0]),
        };
    }

    private const string Usage = """
        用法：
          Agent.Harness.Parity run     --cases <用例目录> --out <输出目录>
          Agent.Harness.Parity compare --cases <用例目录> --legacy <旧引擎结果目录> --maf <新引擎结果目录> [--report <报告.md>]
        """;

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        Console.Error.WriteLine(Usage);
        return 2;
    }

    /// <summary>跑新引擎：每个用例 → 一份 <c>&lt;用例名&gt;.maf.json</c>。</summary>
    private static async Task<int> RunAsync(IReadOnlyDictionary<string, string> options)
    {
        var casesDirectory = Require(options, "cases");
        var outputDirectory = Require(options, "out");

        var cases = ParityCaseFile.LoadAll(casesDirectory);
        if (cases.Count == 0)
        {
            Console.Error.WriteLine($"用例目录里没有 *.json：{casesDirectory}");
            return 1;
        }

        foreach (var parityCase in cases)
        {
            var outcome = await RunCaseAsync(parityCase);
            var path = Path.Combine(outputDirectory, parityCase.Name + ".maf.json");
            ParityCaseFile.Save(path, outcome);

            var status = outcome.Failed ? $"FAILED({outcome.FailureCode})" : "ok";
            Console.WriteLine(
                $"{parityCase.Name,-40} calls={outcome.ModelCalls} tools={outcome.ToolCalls.Count} {status}");
        }

        Console.WriteLine($"\n已写出 {cases.Count} 份新引擎结果 → {Path.GetFullPath(outputDirectory)}");
        return 0;
    }

    private static async Task<TurnOutcome> RunCaseAsync(ParityCase parityCase)
    {
        var model = new SequenceChatModel(parityCase.Calls, parityCase.ModelName);
        var registry = new ToolRegistry(parityCase.Tools.Select(stub => (ITool)new StubTool(stub)));

        // 只读过滤关掉、并发关掉、空正文重试关掉：parity 要的是「同一批模型输出下行为一致」，
        // 灰度默认值（只读 + 重试）属于部署策略，不是引擎语义。
        var engine = new MafAgentEngine(
            model,
            registry,
            new AllowAllPolicy(),
            NullLoggerFactory.Instance,
            new MafAgentEngineOptions
            {
                ReadOnlyToolsOnly = false,
                AllowConcurrentToolInvocation = false,
                MaxEmptyResponseRetries = 0,
                // 宿主侧的收尾轮说明由用例给定：旧引擎的措辞写在 ChatService 里，
                // 用例把它抄一份，EOS 侧测试断言两者逐字相同（防漂移）。
                FinalRoundInstruction = parityCase.FinalRoundInstruction,
            });

        var request = new AgentTurnRequest(
            TenantId: "parity",
            UserId: "parity",
            ConversationId: parityCase.Name,
            Input: parityCase.UserInput,
            History: [],
            SystemPrompt: parityCase.SystemPrompt,
            CorrelationId: parityCase.Name,
            MaxToolRounds: parityCase.MaxToolRounds);

        TurnRun run;
        try
        {
            run = await TurnRun.CollectAsync(engine, request);
        }
        catch (TranscriptMismatchException exception)
        {
            // 用例脚本化的模型调用次数不够：这是用例问题，不是引擎差异，单独标出来。
            return new TurnOutcome(
                parityCase.Name, "maf", model.Calls, string.Empty, null, [], new OutcomeUsage(0, 0),
                model.RequestShapes, "parity_script_exhausted", exception.Message);
        }

        return TurnOutcome.FromTurnRun(parityCase.Name, "maf", run, model.Calls, model.RequestShapes);
    }

    /// <summary>比对两边结果，出 markdown 报告；有 FAIL 时返回非 0。</summary>
    private static int Compare(IReadOnlyDictionary<string, string> options)
    {
        var casesDirectory = Require(options, "cases");
        var legacyDirectory = Require(options, "legacy");
        var mafDirectory = Require(options, "maf");
        options.TryGetValue("report", out var reportPath);

        var cases = ParityCaseFile.LoadAll(casesDirectory);
        if (cases.Count == 0)
        {
            Console.Error.WriteLine($"用例目录里没有 *.json：{casesDirectory}");
            return 1;
        }

        var report = new StringBuilder();
        report.AppendLine("# 引擎 parity 报告");
        report.AppendLine();
        report.AppendLine($"- 用例目录：`{Path.GetFullPath(casesDirectory)}`");
        report.AppendLine($"- 旧引擎结果：`{Path.GetFullPath(legacyDirectory)}`");
        report.AppendLine($"- 新引擎结果：`{Path.GetFullPath(mafDirectory)}`");
        report.AppendLine();
        report.AppendLine("判定分三档：**FAIL** = 真回归（用户可见结果或单据事实不同）；");
        report.AppendLine("**INFO** = 已知差异或观察项；**EXCLUDED** = 明确不比（理由逐条列出）。");
        report.AppendLine();

        var rows = new List<(string Case, IReadOnlyList<ParityDifference> Differences)>();
        var failTotal = 0;
        var missing = 0;

        foreach (var parityCase in cases)
        {
            var legacyPath = Path.Combine(legacyDirectory, parityCase.Name + ".legacy.json");
            var mafPath = Path.Combine(mafDirectory, parityCase.Name + ".maf.json");

            if (!File.Exists(legacyPath) || !File.Exists(mafPath))
            {
                missing++;
                var absent = !File.Exists(legacyPath) ? "旧引擎结果" : "新引擎结果";
                rows.Add((parityCase.Name,
                    [new ParityDifference(ParitySeverity.Fail, "missing_outcome", $"缺少{absent}文件")]));
                failTotal++;
                continue;
            }

            var legacy = ParityCaseFile.LoadOutcome<TurnOutcome>(legacyPath);
            var maf = ParityCaseFile.LoadOutcome<TurnOutcome>(mafPath);
            var differences = TurnOutcomeComparer.Compare(legacy, maf);

            rows.Add((parityCase.Name, differences));
            failTotal += differences.Count(difference => difference.Severity == ParitySeverity.Fail);
        }

        report.AppendLine("## 汇总");
        report.AppendLine();
        report.AppendLine("| 用例 | 判定 | FAIL | INFO | 工具调用 | 模型调用 |");
        report.AppendLine("|---|---|---|---|---|---|");
        foreach (var (caseName, differences) in rows)
        {
            var fails = differences.Count(difference => difference.Severity == ParitySeverity.Fail);
            var infos = differences.Count(difference => difference.Severity == ParitySeverity.Info);
            report.AppendLine($"| {caseName} | {(fails == 0 ? "PASS" : "FAIL")} | {fails} | {infos} | | |");
        }

        report.AppendLine();
        report.AppendLine("## 明细");
        foreach (var (caseName, differences) in rows)
        {
            report.AppendLine();
            report.AppendLine($"### {caseName}");
            report.AppendLine();
            foreach (var difference in differences)
            {
                report.AppendLine($"- `{difference.Severity}` **{difference.Code}**：{difference.Detail}");
            }
        }

        Console.WriteLine($"用例 {rows.Count} 个，FAIL {failTotal} 条，缺结果 {missing} 个。");
        foreach (var (caseName, differences) in rows)
        {
            var fails = differences.Count(difference => difference.Severity == ParitySeverity.Fail);
            var infos = differences.Count(difference => difference.Severity == ParitySeverity.Info);
            Console.WriteLine($"  {caseName,-40} {(fails == 0 ? "PASS" : "FAIL")}  fail={fails} info={infos}");
            foreach (var difference in differences.Where(d => d.Severity == ParitySeverity.Fail))
            {
                Console.WriteLine($"    ! {difference.Code}: {difference.Detail}");
            }
        }

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(reportPath, report.ToString());
            Console.WriteLine($"\n报告 → {Path.GetFullPath(reportPath)}");
        }

        return failTotal == 0 ? 0 : 1;
    }

    private static string Require(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"缺少参数 --{name}\n{Usage}");

    private static IReadOnlyDictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            if (!current.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"无法识别的参数：{current}\n{Usage}");
            }

            var name = current[2..];
            var value = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : string.Empty;

            options[name] = value;
        }

        return options;
    }

    /// <summary>用例里的工具桩：声明逐字来自用例，执行结果固定。</summary>
    private sealed class StubTool(ParityToolStub stub) : ITool
    {
        public ToolDescriptor Descriptor { get; } = new(
            stub.Name,
            stub.Description,
            stub.ParametersJson,
            stub.Risk,
            stub.ReadOnly,
            stub.ReadOnly);

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(stub.Ok
                ? ToolResult.Success(stub.ResultContent)
                : ToolResult.Deny(stub.ResultContent));
    }

    /// <summary>parity 不测策略层（那由宿主实现），这里全放行。</summary>
    private sealed class AllowAllPolicy : IToolPolicy
    {
        public ValueTask<ToolDecision> EvaluateAsync(
            ToolDescriptor tool,
            ToolInvocation invocation,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ToolDecision>(new ToolDecision.Allow());
    }
}
