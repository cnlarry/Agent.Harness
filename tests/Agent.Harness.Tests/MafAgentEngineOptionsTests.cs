using Agent.Harness.Abstractions;
using Agent.Harness.Core;
using Agent.Harness.Maf;
using Agent.Harness.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class MafAgentEngineOptionsTests
{
    [Fact]
    public async Task ReadOnlyMode_OnlySendsReadOnlyTools()
    {
        var recorder = new RecordingChatModel(FakeChatModel.Text("ok"));
        var registry = new ToolRegistry(
        [
            new NamedTool("read", ToolRisk.Read, readOnly: true),
            new NamedTool("draft", ToolRisk.Draft, readOnly: false),
            new NamedTool("write", ToolRisk.Write, readOnly: false),
        ]);

        var engine = new MafAgentEngine(
            recorder,
            registry,
            new AllowAllToolPolicy(),
            NullLoggerFactory.Instance,
            new MafAgentEngineOptions { ReadOnlyToolsOnly = true });

        await TurnRun.CollectAsync(engine, Request());

        var transcript = recorder.Build("readonly");
        var names = Assert.Single(transcript.Calls).Tools!.Select(tool => tool.Name).ToArray();
        Assert.Equal(new[] { "read" }, names);
    }

    [Fact]
    public async Task EnabledTools_RestrictsByAllowlist()
    {
        var recorder = new RecordingChatModel(FakeChatModel.Text("ok"));
        var registry = new ToolRegistry(
        [
            new NamedTool("read", ToolRisk.Read, readOnly: true),
            new NamedTool("draft", ToolRisk.Draft, readOnly: false),
        ]);

        var engine = new MafAgentEngine(
            recorder,
            registry,
            new AllowAllToolPolicy(),
            NullLoggerFactory.Instance,
            new MafAgentEngineOptions
            {
                ReadOnlyToolsOnly = false,
                EnabledTools = ["read"],
            });

        await TurnRun.CollectAsync(engine, Request());

        var transcript = recorder.Build("allowlist");
        var names = Assert.Single(transcript.Calls).Tools!.Select(tool => tool.Name).ToArray();
        Assert.Equal(new[] { "read" }, names);
    }

    [Fact]
    public async Task EnabledTools_EmptyExposesNoTools()
    {
        var recorder = new RecordingChatModel(FakeChatModel.Text("ok"));
        var registry = new ToolRegistry(
        [
            new NamedTool("read", ToolRisk.Read, readOnly: true),
        ]);

        var engine = new MafAgentEngine(
            recorder,
            registry,
            new AllowAllToolPolicy(),
            NullLoggerFactory.Instance,
            new MafAgentEngineOptions
            {
                ReadOnlyToolsOnly = false,
                EnabledTools = [],
            });

        await TurnRun.CollectAsync(engine, Request());

        var transcript = recorder.Build("empty-allowlist");
        Assert.Empty(Assert.Single(transcript.Calls).Tools ?? []);
    }

    [Fact]
    public async Task EmptyResponse_IsRetriedOnce()
    {
        var model = new FakeChatModel(
            [new ChatDelta(null, new ChatUsage(1, 1, 1), FinishReason: "stop")],
            [new ChatDelta("ok", new ChatUsage(1, 1, 1), FinishReason: "stop")]);

        var engine = new MafAgentEngine(
            model,
            new ToolRegistry([]),
            new AllowAllToolPolicy(),
            NullLoggerFactory.Instance,
            new MafAgentEngineOptions());

        var run = await TurnRun.CollectAsync(engine, Request());

        Assert.Equal("ok", run.Text);
        Assert.Equal(2, run.Usage.PromptTokens);
    }

    [Fact]
    public async Task EmptyResponse_WhenRetriesDisabled_StaysEmpty()
    {
        var model = new FakeChatModel(
            [new ChatDelta(null, new ChatUsage(1, 1, 1), FinishReason: "stop")],
            [new ChatDelta("unused", new ChatUsage(1, 1, 1), FinishReason: "stop")]);

        var engine = new MafAgentEngine(
            model,
            new ToolRegistry([]),
            new AllowAllToolPolicy(),
            NullLoggerFactory.Instance,
            new MafAgentEngineOptions { MaxEmptyResponseRetries = 0 });

        var run = await TurnRun.CollectAsync(engine, Request());

        Assert.Empty(run.Text);
        Assert.Equal(1, run.Usage.PromptTokens);
    }

    [Fact]
    public void AddAgentHarnessMaf_BindsConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentHarness:Maf:ReadOnlyToolsOnly"] = "false",
                ["AgentHarness:Maf:AllowConcurrentToolInvocation"] = "true",
                ["AgentHarness:Maf:MaxEmptyResponseRetries"] = "2",
                ["AgentHarness:Maf:EnabledTools:0"] = "read",
                ["AgentHarness:Maf:EnabledTools:1"] = "draft",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddAgentHarnessMaf(configuration.GetSection("AgentHarness:Maf"));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<MafAgentEngineOptions>();

        Assert.False(options.ReadOnlyToolsOnly);
        Assert.True(options.AllowConcurrentToolInvocation);
        Assert.Equal(2, options.MaxEmptyResponseRetries);
        Assert.Equal(new[] { "read", "draft" }, options.EnabledTools);
    }

    [Fact]
    public async Task AddAgentHarnessMaf_DiDefaultsToReadOnlyFiltering()
    {
        var recorder = new RecordingChatModel(FakeChatModel.Text("ok"));
        var services = new ServiceCollection();
        services.AddSingleton<IChatModel>(recorder);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddScoped<IToolPolicy, AllowAllToolPolicy>();
        services.AddScoped<ITool>(_ => new NamedTool("read", ToolRisk.Read, readOnly: true));
        services.AddScoped<ITool>(_ => new NamedTool("write", ToolRisk.Write, readOnly: false));
        services.AddAgentHarnessCore();
        services.AddAgentHarnessMaf();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAgentEngine>();

        await TurnRun.CollectAsync(engine, Request());

        var transcript = recorder.Build("di-readonly");
        var names = Assert.Single(transcript.Calls).Tools!.Select(tool => tool.Name).ToArray();
        Assert.Equal(new[] { "read" }, names);
    }

    [Fact]
    public void AddAgentHarnessMaf_DefaultsToReadOnlyAndSerial()
    {
        var services = new ServiceCollection();
        services.AddAgentHarnessMaf();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<MafAgentEngineOptions>();

        Assert.True(options.ReadOnlyToolsOnly);
        Assert.False(options.AllowConcurrentToolInvocation);
        Assert.Equal(1, options.MaxEmptyResponseRetries);
        Assert.Null(options.EnabledTools);
    }

    [Fact]
    public void AddAgentHarnessMaf_CanOptIntoAllTools()
    {
        var services = new ServiceCollection();
        services.AddAgentHarnessMaf(options => options.ReadOnlyToolsOnly = false);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<MafAgentEngineOptions>();

        Assert.False(options.ReadOnlyToolsOnly);
    }

    [Fact]
    public async Task AllowConcurrentInvocation_False_ExecutesSerially()
    {
        var tool = new ConcurrencyProbeTool();
        var (engine, _) = CreateProbeEngine(tool, concurrent: false);

        var run = await TurnRun.CollectAsync(engine, Request());

        Assert.Equal(2, run.ToolFinished.Count);
        Assert.Equal(2, tool.Calls);
        Assert.Equal(1, tool.MaxConcurrent);
    }

    [Fact]
    public async Task AllowConcurrentInvocation_True_ExecutesConcurrently()
    {
        var tool = new ConcurrencyProbeTool();
        var (engine, _) = CreateProbeEngine(tool, concurrent: true);

        var run = await TurnRun.CollectAsync(engine, Request());

        Assert.Equal(2, run.ToolFinished.Count);
        Assert.Equal(2, tool.Calls);
        Assert.Equal(2, tool.MaxConcurrent);
    }

    private static (MafAgentEngine Engine, ToolRegistry Registry) CreateProbeEngine(
        ConcurrencyProbeTool tool,
        bool concurrent)
    {
        var model = new FakeChatModel(
            [
                new ChatDelta(
                    ContentDelta: null,
                    Usage: null,
                    ToolCallDeltas:
                    [
                        new ProposedToolCallFragment(0, "call_1", "probe", "{}"),
                        new ProposedToolCallFragment(1, "call_2", "probe", "{}"),
                    ],
                    FinishReason: "tool_calls"),
            ],
            [new ChatDelta("done", null, FinishReason: "stop")]);

        var registry = new ToolRegistry([tool]);
        var engine = new MafAgentEngine(
            model,
            registry,
            new AllowAllToolPolicy(),
            NullLoggerFactory.Instance,
            new MafAgentEngineOptions
            {
                ReadOnlyToolsOnly = false,
                AllowConcurrentToolInvocation = concurrent,
            });

        return (engine, registry);
    }

    private static AgentTurnRequest Request() => new(
        TenantId: "tenant",
        UserId: "user",
        ConversationId: "conversation",
        Input: "hi",
        History: [],
        SystemPrompt: "system",
        CorrelationId: "correlation");

    private sealed class NamedTool(string name, ToolRisk risk, bool readOnly) : ITool
    {
        public ToolDescriptor Descriptor { get; } = new(
            name,
            name,
            """{"type":"object","properties":{}}""",
            risk,
            readOnly);

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ToolResult.Success("ok"));
    }

    private sealed class ConcurrencyProbeTool : ITool
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<bool> _bothStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _active;
        private int _maxConcurrent;

        public int Calls { get; private set; }

        public int MaxConcurrent
        {
            get
            {
                lock (_gate)
                {
                    return _maxConcurrent;
                }
            }
        }

        public ToolDescriptor Descriptor { get; } = new(
            "probe",
            "并发探针。",
            """{"type":"object","properties":{}}""",
            ToolRisk.Read,
            ReadOnly: true);

        public async ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Calls++;
                _active++;
                _maxConcurrent = Math.Max(_maxConcurrent, _active);

                if (_active == 2)
                {
                    _bothStarted.TrySetResult(true);
                }
            }

            try
            {
                await Task.WhenAny(
                    _bothStarted.Task,
                    Task.Delay(150, cancellationToken));
            }
            finally
            {
                lock (_gate)
                {
                    _active--;
                }
            }

            return ToolResult.Success("ok");
        }
    }
}
