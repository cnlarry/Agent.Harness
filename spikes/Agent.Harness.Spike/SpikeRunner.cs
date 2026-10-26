using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Harness.Abstractions;
using Agent.Harness.Core;
using Agent.Harness.Maf;
using Agent.Harness.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent.Harness.Spike;

/// <summary>P0 的 10 条用例。真实模型用例与本地桩用例分开，报告中如实标注。</summary>
internal static class SpikeRunner
{
    private const string SystemPrompt =
        "你是 Agent Harness 的 P0 测试助手。回答简洁；需要业务数据时必须调用工具，不要编造。";

    public static async Task<int> RunAsync(string[] args)
    {
        var stubOnly = args.Contains("--stub-only", StringComparer.OrdinalIgnoreCase);
        var baseline = args.Contains("--baseline", StringComparer.OrdinalIgnoreCase);
        var modelName = GetOption(args, "--model") ?? "deepseek-flash";
        var caseFilter = GetOption(args, "--case")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var options = new DeepSeekOptions(Model: modelName);
        var results = new List<CaseResult>();

        if (baseline && stubOnly)
        {
            Console.Error.WriteLine("--baseline 需要真实模型，不能和 --stub-only 一起使用。");
            return 2;
        }

        bool ShouldRun(string id) =>
            caseFilter is null || caseFilter.Contains(id, StringComparer.OrdinalIgnoreCase);

        Console.WriteLine(baseline ? "Agent Harness P2 Baseline" : "Agent Harness P0 Spike");
        Console.WriteLine($"model    : {options.Model}");
        Console.WriteLine($"endpoint : {options.Endpoint}");
        Console.WriteLine($"key env  : {options.ApiKeyEnvironmentVariable}");
        Console.WriteLine();

        if (!stubOnly &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(options.ApiKeyEnvironmentVariable)))
        {
            Console.Error.WriteLine(
                $"缺少环境变量 {options.ApiKeyEnvironmentVariable}。设置后重试，或加 --stub-only 只跑本地用例。");
            return 2;
        }

        using var model = new DeepSeekChatModel(options);

        if (baseline)
        {
            return await RunP2BaselineAsync(model, options, args);
        }

        if (!stubOnly)
        {
            if (ShouldRun("01") || ShouldRun("06") || ShouldRun("07"))
            {
                await RunBaselineAsync(model, results);
            }

            if (ShouldRun("02") || ShouldRun("05"))
            {
                await RunSingleToolAsync(model, results);
            }

            if (ShouldRun("03"))
            {
                await RunMultiRoundAsync(model, results);
            }

            if (ShouldRun("04"))
            {
                await RunParallelAsync(model, results);
            }

            if (ShouldRun("08"))
            {
                await RunCancellationAsync(model, results);
            }
        }

        if (ShouldRun("09"))
        {
            await RunErrorMappingAsync(options, results);
        }

        if (ShouldRun("10"))
        {
            await RunMaxRoundsAsync(results);
        }

        PrintSummary(results);
        WriteResults(results, options);
        return results.All(result => result.Passed) ? 0 : 1;
    }

    private static async Task<int> RunP2BaselineAsync(
        DeepSeekChatModel model,
        DeepSeekOptions options,
        string[] args)
    {
        var count = Math.Clamp(GetIntOption(args, "--baseline-count", 100), 2, 500);
        var warmup = Math.Clamp(GetIntOption(args, "--baseline-warmup", 2), 0, 20);
        var textCount = count / 2;
        var toolCount = count - textCount;
        var inputPrice = GetDecimalOption(args, "--input-price-per-1m");
        var outputPrice = GetDecimalOption(args, "--output-price-per-1m");
        var currency = GetOption(args, "--currency") ?? "USD";

        Console.WriteLine($"attempts : {count} ({textCount} text, {toolCount} tool)");
        Console.WriteLine($"warmup   : {warmup}");
        Console.WriteLine();

        for (var index = 0; index < warmup; index++)
        {
            var kind = index % 2 == 0 ? "text" : "tool";
            var warmupSample = await RunP2BaselineSampleAsync(model, kind);
            Console.WriteLine(
                $"[warmup {index + 1}/{warmup}] {kind,-4} {warmupSample.ElapsedMs,6} ms " +
                $"completed={warmupSample.Completed}");
        }

        if (warmup > 0)
        {
            Console.WriteLine();
        }

        var samples = new List<BaselineSample>();
        var textUsed = 0;
        var toolUsed = 0;

        for (var index = 0; index < count; index++)
        {
            var kind = index % 2 == 0 ? "text" : "tool";
            if (kind == "text" && textUsed >= textCount)
            {
                kind = "tool";
            }
            else if (kind == "tool" && toolUsed >= toolCount)
            {
                kind = "text";
            }

            if (kind == "text")
            {
                textUsed++;
            }
            else
            {
                toolUsed++;
            }

            var sample = await RunP2BaselineSampleAsync(model, kind);
            samples.Add(sample);
            Console.WriteLine(
                $"[{index + 1:00}/{count}] {kind,-4} {sample.ElapsedMs,6} ms " +
                $"tokens={sample.PromptTokens}+{sample.CompletionTokens} tools={sample.ToolCalls} " +
                $"completed={sample.Completed} marker={sample.MarkerMatched}");
        }

        var summary = new BaselineSummary(
            Overall: BuildBaselineGroup("all", samples, inputPrice, outputPrice),
            Text: BuildBaselineGroup("text", samples, inputPrice, outputPrice),
            Tool: BuildBaselineGroup("tool", samples, inputPrice, outputPrice));

        Console.WriteLine();
        Console.WriteLine($"currency : {currency}");
        PrintBaselineGroup(summary.Overall);
        PrintBaselineGroup(summary.Text);
        PrintBaselineGroup(summary.Tool);

        WriteBaselineResults(options, samples, summary, inputPrice, outputPrice, currency);
        return summary.Overall.Completed > 0 ? 0 : 1;
    }

    private static BaselineGroup BuildBaselineGroup(
        string kind,
        IReadOnlyList<BaselineSample> samples,
        decimal? inputPrice,
        decimal? outputPrice)
    {
        var items = kind == "all"
            ? samples.ToArray()
            : samples.Where(sample => sample.Kind == kind).ToArray();
        var completed = items.Where(sample => sample.Completed).ToArray();
        var promptTokens = completed.Sum(sample => sample.PromptTokens);
        var completionTokens = completed.Sum(sample => sample.CompletionTokens);

        decimal? estimatedCost = inputPrice is not null && outputPrice is not null
            ? promptTokens / 1_000_000m * inputPrice.Value +
              completionTokens / 1_000_000m * outputPrice.Value
            : null;

        var toolDenominator = kind == "tool" ? items.Length : 0;

        return new BaselineGroup(
            Kind: kind,
            Attempts: items.Length,
            Completed: completed.Length,
            Failures: items.Length - completed.Length,
            MarkerMatches: completed.Count(sample => sample.MarkerMatched),
            EmptyText: completed.Count(sample => sample.TextLength == 0),
            P50Ms: Percentile(completed.Select(sample => sample.ElapsedMs).ToArray(), 50),
            P95Ms: Percentile(completed.Select(sample => sample.ElapsedMs).ToArray(), 95),
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            ToolCallRate: toolDenominator == 0
                ? 0
                : items.Count(sample => sample.ToolCalls > 0) / (double)toolDenominator,
            EstimatedCost: estimatedCost);
    }

    private static void PrintBaselineGroup(BaselineGroup group)
    {
        Console.WriteLine(
            $"{group.Kind,-5}: completed {group.Completed}/{group.Attempts}, failures {group.Failures}, " +
            $"marker {group.MarkerMatches}, emptyText {group.EmptyText}, toolRate {group.ToolCallRate:P0}");
        Console.WriteLine(
            $"        p50/p95 {group.P50Ms?.ToString("0", CultureInfo.InvariantCulture) ?? "n/a"}/" +
            $"{group.P95Ms?.ToString("0", CultureInfo.InvariantCulture) ?? "n/a"} ms, " +
            $"tokens {group.PromptTokens}+{group.CompletionTokens}, " +
            $"cost {(group.EstimatedCost?.ToString("0.######", CultureInfo.InvariantCulture) ?? "n/a")}");
    }

    private static async Task<BaselineSample> RunP2BaselineSampleAsync(
        DeepSeekChatModel model,
        string kind)
    {
        var tools = new SpikeTools();
        var registry = kind == "tool" ? new ToolRegistry(tools.All) : new ToolRegistry([]);
        var engine = new MafAgentEngine(
            model,
            registry,
            new AllowAllToolPolicy(),
            NullLoggerFactory.Instance,
            new MafAgentEngineOptions
            {
                ReadOnlyToolsOnly = true,
                EnabledTools = kind == "tool"
                    ? new[] { "get_order" }
                    : Array.Empty<string>(),
            });

        var pipeline = new TurnPipeline(
            engine,
            new InMemoryTurnBudget(),
            new InMemoryAuditSink());

        var prompt = kind == "tool"
            ? "请调用 get_order 查询订单 SO-1001，然后回复订单状态。"
            : "只回复：P2-BASELINE-OK";

        var request = new AgentTurnRequest(
            TenantId: "baseline-tenant",
            UserId: "baseline-user",
            ConversationId: $"baseline-{Guid.NewGuid():N}",
            Input: prompt,
            History: [],
            SystemPrompt: SystemPrompt,
            CorrelationId: Guid.NewGuid().ToString("N"),
            MaxToolRounds: 4);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var run = new TurnRun();
            await foreach (var evt in pipeline
                .StreamAsync(request)
                .ConfigureAwait(false))
            {
                run.Events.Add(evt);
            }
            stopwatch.Stop();

            var completed = run.Completed;
            var failed = run.Events.OfType<AgentEvent.Failed>().LastOrDefault();
            var text = completed?.Result.Text ?? string.Empty;
            var toolCalls = completed?.Result.ToolCalls.Count ?? 0;

            return new BaselineSample(
                Kind: kind,
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                PromptTokens: completed?.Result.Usage.PromptTokens ?? 0,
                CompletionTokens: completed?.Result.Usage.CompletionTokens ?? 0,
                ToolCalls: toolCalls,
                FinishReason: completed?.Result.FinishReason,
                Completed: completed is not null,
                TextLength: text.Length,
                MarkerMatched: kind == "text" &&
                    text.Contains("P2-BASELINE-OK", StringComparison.Ordinal),
                Error: failed is null ? null : $"{failed.Code}: {failed.Message}");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return new BaselineSample(
                Kind: kind,
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                PromptTokens: 0,
                CompletionTokens: 0,
                ToolCalls: 0,
                FinishReason: null,
                Completed: false,
                TextLength: 0,
                MarkerMatched: false,
                Error: $"{exception.GetType().Name}: {Truncate(exception.Message, 120)}");
        }
    }

    private static void WriteBaselineResults(
        DeepSeekOptions options,
        IReadOnlyList<BaselineSample> samples,
        BaselineSummary summary,
        decimal? inputPrice,
        decimal? outputPrice,
        string currency)
    {
        try
        {
            var directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "baseline");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"p2-baseline-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");

            var payload = new
            {
                generatedAt = DateTimeOffset.Now,
                model = options.Model,
                endpoint = options.Endpoint,
                count = samples.Count,
                inputPricePer1M = inputPrice,
                outputPricePer1M = outputPrice,
                currency = currency,
                summary = new
                {
                    overall = new
                    {
                        summary.Overall.Kind,
                        summary.Overall.Attempts,
                        summary.Overall.Completed,
                        summary.Overall.Failures,
                        summary.Overall.MarkerMatches,
                        summary.Overall.EmptyText,
                        summary.Overall.P50Ms,
                        summary.Overall.P95Ms,
                        summary.Overall.PromptTokens,
                        summary.Overall.CompletionTokens,
                        summary.Overall.ToolCallRate,
                        summary.Overall.EstimatedCost,
                    },
                    text = new
                    {
                        summary.Text.Kind,
                        summary.Text.Attempts,
                        summary.Text.Completed,
                        summary.Text.Failures,
                        summary.Text.MarkerMatches,
                        summary.Text.EmptyText,
                        summary.Text.P50Ms,
                        summary.Text.P95Ms,
                        summary.Text.PromptTokens,
                        summary.Text.CompletionTokens,
                        summary.Text.ToolCallRate,
                        summary.Text.EstimatedCost,
                    },
                    tool = new
                    {
                        summary.Tool.Kind,
                        summary.Tool.Attempts,
                        summary.Tool.Completed,
                        summary.Tool.Failures,
                        summary.Tool.MarkerMatches,
                        summary.Tool.EmptyText,
                        summary.Tool.P50Ms,
                        summary.Tool.P95Ms,
                        summary.Tool.PromptTokens,
                        summary.Tool.CompletionTokens,
                        summary.Tool.ToolCallRate,
                        summary.Tool.EstimatedCost,
                    },
                },
                samples = samples
                    .Select(sample => new
                    {
                        sample.Kind,
                        sample.ElapsedMs,
                        sample.PromptTokens,
                        sample.CompletionTokens,
                        sample.ToolCalls,
                        sample.FinishReason,
                        sample.Completed,
                        sample.TextLength,
                        sample.MarkerMatched,
                        sample.Error,
                    })
                    .ToArray(),
            };

            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
            Console.WriteLine($"results  : {path}");
        }
        catch (IOException exception)
        {
            Console.WriteLine($"results write skipped: {exception.Message}");
        }
    }

    private static double? Percentile(IReadOnlyList<long> values, double percentile)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var ordered = values.OrderBy(value => value).ToArray();
        var rank = (int)Math.Ceiling(percentile / 100.0 * ordered.Length) - 1;
        return ordered[Math.Clamp(rank, 0, ordered.Length - 1)];
    }

    private static async Task RunBaselineAsync(DeepSeekChatModel model, List<CaseResult> results)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var engine = CreateEngine(model, new ToolRegistry([]));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var run = await RunAsync(
                engine,
                NewRequest("只回复这四个字：P0-TEXT-OK"),
                cancellation.Token);

            stopwatch.Stop();
            var text = run.Text.Trim();
            var reasoning = string.Concat(run.ReasoningDeltas.Select(delta => delta.Text));

            var textOk = text.Contains("P0-TEXT-OK", StringComparison.Ordinal) && run.TextDeltas.Count > 0;
            results.Add(new CaseResult(
                "01",
                "单轮文本",
                textOk,
                $"text=\"{Truncate(text, 80)}\"",
                stopwatch.ElapsedMilliseconds));

            results.Add(new CaseResult(
                "06",
                "reasoning_content 透传",
                reasoning.Length > 0,
                $"reasoningChars={reasoning.Length}",
                stopwatch.ElapsedMilliseconds));

            var usage = run.Usage;
            var usageOk = usage.PromptTokens > 0 &&
                          usage.CompletionTokens > 0 &&
                          usage.TotalTokens == usage.PromptTokens + usage.CompletionTokens;
            results.Add(new CaseResult(
                "07",
                "usage 统计",
                usageOk,
                $"prompt={usage.PromptTokens} completion={usage.CompletionTokens} total={usage.TotalTokens}",
                stopwatch.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            results.Add(new CaseResult("01", "单轮文本", false, Describe(ex), stopwatch.ElapsedMilliseconds));
            results.Add(new CaseResult("06", "reasoning_content 透传", false, "baseline failed", stopwatch.ElapsedMilliseconds));
            results.Add(new CaseResult("07", "usage 统计", false, "baseline failed", stopwatch.ElapsedMilliseconds));
        }
    }

    private static async Task RunSingleToolAsync(DeepSeekChatModel model, List<CaseResult> results)
    {
        var stopwatch = Stopwatch.StartNew();
        var tools = new SpikeTools();
        try
        {
            var engine = CreateEngine(model, new ToolRegistry(tools.All));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var run = await RunAsync(
                engine,
                NewRequest("请调用 get_order 查询订单 SO-1001，然后根据工具结果告诉我订单状态。"),
                cancellation.Token);

            stopwatch.Stop();
            var callId = tools.CallIds.FirstOrDefault() ?? string.Empty;
            var eventsOk = run.ToolStarted.Count == 1 &&
                           run.ToolFinished.Count == 1 &&
                           run.Completed is not null;
            var textOk = run.Text.Contains("发货", StringComparison.Ordinal);
            var callIdOk = tools.GetOrderCalls == 1 && !string.IsNullOrWhiteSpace(callId);
            results.Add(new CaseResult(
                "02",
                "单工具调用",
                eventsOk && textOk && callIdOk,
                $"calls={tools.GetOrderCalls} callId={(callId.Length == 0 ? "<empty>" : callId)} text=\"{Truncate(run.Text, 60)}\"",
                stopwatch.ElapsedMilliseconds));

            var schemaOk = CheckSchema(tools, model.LastRequestJson, out var schemaDetail);
            results.Add(new CaseResult(
                "05",
                "工具 Schema 原样保留",
                schemaOk,
                schemaDetail,
                stopwatch.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            results.Add(new CaseResult("02", "单工具调用", false, Describe(ex), stopwatch.ElapsedMilliseconds));
            results.Add(new CaseResult("05", "工具 Schema 原样保留", false, "single-tool run failed", stopwatch.ElapsedMilliseconds));
        }
    }

    private static async Task RunMultiRoundAsync(DeepSeekChatModel model, List<CaseResult> results)
    {
        var stopwatch = Stopwatch.StartNew();
        var tools = new SpikeTools();
        try
        {
            var engine = CreateEngine(model, new ToolRegistry(tools.All));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            var run = await RunAsync(
                engine,
                NewRequest(
                    "从订单 SO-1001 开始，按工具返回的 next 连续查询，直到 next=无。" +
                    "必须等上一个工具结果再查下一个，不要一次查多个；最后列出每个订单的状态。"),
                cancellation.Token);

            stopwatch.Stop();
            var startIndexes = run.Events
                .Select((evt, index) => (evt, index))
                .Where(item => item.evt is AgentEvent.ToolStarted)
                .Select(item => item.index)
                .ToArray();
            var finishesBeforeThirdStart = startIndexes.Length >= 3
                ? run.Events.Take(startIndexes[2]).Count(evt => evt is AgentEvent.ToolFinished)
                : 0;

            var sequenceOk = tools.OrderSequence.Count >= 3 &&
                             tools.OrderSequence[0] == "SO-1001" &&
                             tools.OrderSequence[1] == "SO-1002" &&
                             tools.OrderSequence[2] == "SO-1003";
            var pass = sequenceOk &&
                       finishesBeforeThirdStart >= 2 &&
                       run.Completed is not null;

            results.Add(new CaseResult(
                "03",
                "多轮工具调用（3 轮）",
                pass,
                $"calls={tools.GetOrderCalls} sequence={string.Join(" -> ", tools.OrderSequence)} finishesBefore3rdStart={finishesBeforeThirdStart}",
                stopwatch.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            results.Add(new CaseResult("03", "多轮工具调用（3 轮）", false, Describe(ex), stopwatch.ElapsedMilliseconds));
        }
    }

    private static async Task RunParallelAsync(DeepSeekChatModel model, List<CaseResult> results)
    {
        var stopwatch = Stopwatch.StartNew();
        var tools = new SpikeTools();
        try
        {
            var engine = CreateEngine(model, new ToolRegistry(tools.All));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var run = await RunAsync(
                engine,
                NewRequest("请同时查询订单 SO-2001 和 SO-2002；可以在一轮里发起两个工具调用。最后分别告诉我状态。"),
                cancellation.Token);

            stopwatch.Stop();
            var firstFinishIndex = run.Events.FindIndex(evt => evt is AgentEvent.ToolFinished);
            var startsBeforeFirstFinish = firstFinishIndex < 0
                ? run.Events.Count(evt => evt is AgentEvent.ToolStarted)
                : run.Events.Take(firstFinishIndex).Count(evt => evt is AgentEvent.ToolStarted);

            var pass = tools.GetOrderCalls == 2 &&
                       startsBeforeFirstFinish == 2 &&
                       run.Completed is not null;

            results.Add(new CaseResult(
                "04",
                "一轮多个工具调用",
                pass,
                $"calls={tools.GetOrderCalls} startsBeforeFirstFinish={startsBeforeFirstFinish} text=\"{Truncate(run.Text, 60)}\"",
                stopwatch.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var messages = DescribeMessages(model.LastRequestJson);
            Console.WriteLine($"---- case 04 last request ----");
            Console.WriteLine(messages);
            Console.WriteLine("-----------------------------");
            results.Add(new CaseResult("04", "一轮多个工具调用", false, $"{Describe(ex)} | {messages}", stopwatch.ElapsedMilliseconds));
        }
    }

    private static async Task RunCancellationAsync(DeepSeekChatModel model, List<CaseResult> results)
    {
        var stopwatch = Stopwatch.StartNew();
        var run = new RunResult();
        var canceled = false;
        try
        {
            var engine = CreateEngine(model, new ToolRegistry([]));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await foreach (var evt in engine
                .StreamAsync(
                    NewRequest("请用至少 500 字详细解释什么是幂等，并给出三个例子。"),
                    cancellation.Token)
                .ConfigureAwait(false))
            {
                run.Events.Add(evt);
                if (evt is AgentEvent.TextDelta or AgentEvent.ReasoningDelta)
                {
                    cancellation.Cancel();
                }
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        stopwatch.Stop();
        var pass = canceled &&
                   stopwatch.Elapsed < TimeSpan.FromSeconds(15) &&
                   run.Completed is null;

        results.Add(new CaseResult(
            "08",
            "取消/断流",
            pass,
            $"canceled={canceled} elapsedMs={stopwatch.ElapsedMilliseconds} completed={run.Completed is not null}",
            stopwatch.ElapsedMilliseconds));
    }

    private static async Task RunErrorMappingAsync(DeepSeekOptions options, List<CaseResult> results)
    {
        var stopwatch = Stopwatch.StartNew();
        var cases = new (HttpStatusCode Status, string Code)[]
        {
            (HttpStatusCode.Unauthorized, "auth_failed"),
            (HttpStatusCode.PaymentRequired, "insufficient_balance"),
            (HttpStatusCode.TooManyRequests, "rate_limited"),
            (HttpStatusCode.RequestEntityTooLarge, "payload_too_large"),
            (HttpStatusCode.InternalServerError, "provider_error"),
        };

        var failures = new List<string>();
        foreach (var item in cases)
        {
            using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(item.Status)
            {
                Content = new StringContent(
                    """{"error":{"message":"stub failure"}}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
            using var http = new HttpClient(handler);
            using var model = new DeepSeekChatModel(options, http, apiKey: "stub-key");

            try
            {
                await foreach (var _ in model
                    .StreamAsync([new ChatMessage(ChatRole.User, "hi")], null)
                    .ConfigureAwait(false))
                {
                }

                failures.Add($"{(int)item.Status}: no exception");
            }
            catch (DeepSeekApiException ex)
            {
                if (ex.StatusCode != item.Status ||
                    ex.Code != item.Code ||
                    ex.Message.Contains("stub-key", StringComparison.Ordinal))
                {
                    failures.Add($"{(int)item.Status}: got {ex.Code}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{(int)item.Status}: {ex.GetType().Name}");
            }
        }

        stopwatch.Stop();
        results.Add(new CaseResult(
            "09",
            "401/402/429/413/5xx 错误映射",
            failures.Count == 0,
            failures.Count == 0 ? "5/5 mapped" : string.Join("; ", failures),
            stopwatch.ElapsedMilliseconds));
    }

    private static async Task RunMaxRoundsAsync(List<CaseResult> results)
    {
        var stopwatch = Stopwatch.StartNew();
        var scripted = new ScriptedToolCallModel();
        var tools = new SpikeTools();
        RunResult? run = null;
        Exception? error = null;

        try
        {
            var engine = CreateEngine(scripted, new ToolRegistry(tools.All));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            run = await RunAsync(
                engine,
                NewRequest("持续调用 get_order，不要停。", maxRounds: 2),
                cancellation.Token);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        stopwatch.Stop();
        var timedOut = error is OperationCanceledException;
        var controlledStop = error is not null && !timedOut;
        var eventsClosed = run is null || run.ToolStarted.Count == run.ToolFinished.Count;
        var finishOk = run is null ||
                       run.Completed?.Result.FinishReason == AgentFinishReasons.MaxToolRounds;
        var pass = !timedOut &&
                   (run is not null || controlledStop) &&
                   scripted.Calls <= 4 &&
                   eventsClosed &&
                   finishOk;

        var detail = run is not null
            ? $"completed modelCalls={scripted.Calls} toolCalls={tools.GetOrderCalls} " +
              $"started={run.ToolStarted.Count} finished={run.ToolFinished.Count} closed={eventsClosed} " +
              $"textChars={run.Text.Length} finish={run.Completed?.Result.FinishReason ?? "<null>"}"
            : error is not null
                ? $"{error.GetType().Name}: {Truncate(error.Message, 100)} modelCalls={scripted.Calls} toolCalls={tools.GetOrderCalls}"
                : "no run and no error";

        results.Add(new CaseResult(
            "10",
            "最大轮次收尾",
            pass,
            detail,
            stopwatch.ElapsedMilliseconds));
    }

    private static MafAgentEngine CreateEngine(IChatModel model, ToolRegistry registry) =>
        new(model, registry, new AllowAllToolPolicy(), NullLoggerFactory.Instance);

    private static AgentTurnRequest NewRequest(string input, int maxRounds = 4) => new(
        TenantId: "spike-tenant",
        UserId: "spike-user",
        ConversationId: "spike-conversation",
        Input: input,
        History: [],
        SystemPrompt: SystemPrompt,
        CorrelationId: Guid.NewGuid().ToString("N"),
        MaxToolRounds: maxRounds);

    private static async Task<RunResult> RunAsync(
        IAgentEngine engine,
        AgentTurnRequest request,
        CancellationToken cancellationToken)
    {
        var run = new RunResult();
        await foreach (var evt in engine
            .StreamAsync(request, cancellationToken)
            .ConfigureAwait(false))
        {
            run.Events.Add(evt);
        }

        return run;
    }

    private static bool CheckSchema(SpikeTools tools, string? requestJson, out string detail)
    {
        if (requestJson is null)
        {
            detail = "no request captured";
            return false;
        }

        var root = JsonNode.Parse(requestJson);
        var sent = root?["tools"]?.AsArray();
        if (sent is null || sent.Count != tools.All.Length)
        {
            detail = $"sent={sent?.Count ?? 0} expected={tools.All.Length}";
            return false;
        }

        for (var index = 0; index < tools.All.Length; index++)
        {
            var descriptor = tools.All[index].Descriptor;
            var function = sent[index]?["function"];
            var name = function?["name"]?.GetValue<string>();
            var expected = JsonNode.Parse(descriptor.ParametersJson);

            if (name != descriptor.Name || !JsonNode.DeepEquals(function?["parameters"], expected))
            {
                detail = $"mismatch at {descriptor.Name}";
                return false;
            }
        }

        detail = $"{tools.All.Length}/{tools.All.Length} schemas equal";
        return true;
    }

    private static void PrintSummary(List<CaseResult> results)
    {
        Console.WriteLine();
        foreach (var result in results)
        {
            var status = result.Passed ? "PASS" : "FAIL";
            Console.WriteLine($"[{status}] {result.Id} {result.Name} ({result.ElapsedMs} ms) {result.Detail}");
        }

        var passed = results.Count(result => result.Passed);
        Console.WriteLine();
        Console.WriteLine($"{passed}/{results.Count} passed");
    }

    private static void WriteResults(List<CaseResult> results, DeepSeekOptions options)
    {
        try
        {
            var directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "spike");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "p0-results.json");
            var payload = new
            {
                generatedAt = DateTimeOffset.Now,
                model = options.Model,
                endpoint = options.Endpoint,
                results = results.Select(result => new
                {
                    result.Id,
                    result.Name,
                    result.Passed,
                    result.Detail,
                    result.ElapsedMs,
                }).ToArray(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
            Console.WriteLine($"results  : {path}");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"results write skipped: {ex.Message}");
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int GetIntOption(string[] args, string name, int defaultValue)
    {
        var value = GetOption(args, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static decimal? GetDecimalOption(string[] args, string name)
    {
        var value = GetOption(args, name);
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string Describe(Exception exception) =>
        $"{exception.GetType().Name}: {Truncate(exception.Message, 160)}";

    private static string DescribeMessages(string? requestJson)
    {
        if (requestJson is null)
        {
            return "no request captured";
        }

        var root = JsonNode.Parse(requestJson);
        var messages = root?["messages"]?.AsArray();
        if (messages is null)
        {
            return "no messages";
        }

        var parts = new List<string>();
        foreach (var message in messages)
        {
            var role = message?["role"]?.GetValue<string>() ?? "?";
            var text = $"role={role}";

            if (message?["tool_calls"] is JsonArray calls)
            {
                var ids = calls
                    .Select(call => call?["id"]?.GetValue<string>() ?? "?")
                    .ToArray();
                text += $" calls=[{string.Join(",", ids)}]";
            }

            var toolCallId = message?["tool_call_id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(toolCallId))
            {
                text += $" tool_call_id={toolCallId}";
            }

            parts.Add(text);
        }

        return string.Join(" | ", parts);
    }

    private static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Length <= max
                ? text
                : text[..max] + "…";

    private sealed record BaselineSample(
        string Kind,
        long ElapsedMs,
        int PromptTokens,
        int CompletionTokens,
        int ToolCalls,
        string? FinishReason,
        bool Completed,
        int TextLength,
        bool MarkerMatched,
        string? Error);

    private sealed record BaselineGroup(
        string Kind,
        int Attempts,
        int Completed,
        int Failures,
        int MarkerMatches,
        int EmptyText,
        double? P50Ms,
        double? P95Ms,
        int PromptTokens,
        int CompletionTokens,
        double ToolCallRate,
        decimal? EstimatedCost);

    private sealed record BaselineSummary(
        BaselineGroup Overall,
        BaselineGroup Text,
        BaselineGroup Tool);

    private sealed record CaseResult(
        string Id,
        string Name,
        bool Passed,
        string Detail,
        long ElapsedMs);

    private sealed class RunResult
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
    }
}
