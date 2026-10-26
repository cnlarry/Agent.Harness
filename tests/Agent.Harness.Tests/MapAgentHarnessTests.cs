using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using Agent.Harness.Abstractions;
using Agent.Harness.AspNetCore;
using Agent.Harness.Core;
using Agent.Harness.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class MapAgentHarnessTests
{
    [Fact]
    public async Task MapAgentHarness_UsesClaimsAndPipeline_NotRequestBodyIdentity()
    {
        var store = new InMemoryConversationStore();
        await store.SaveUserMessageAsync(
            "tenant-1",
            "user-1",
            "c1",
            "previous",
            "correlation");

        var engine = new CapturingEchoEngine();
        var result = await InvokeAsync(engine, store);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Contains("event: delta", result.Body, StringComparison.Ordinal);
        Assert.Contains("Echo: hi", result.Body, StringComparison.Ordinal);
        Assert.Contains("event: done", result.Body, StringComparison.Ordinal);

        var turn = Assert.IsType<AgentTurnRequest>(engine.Request);
        Assert.Equal("tenant-1", turn.TenantId);
        Assert.Equal("user-1", turn.UserId);
        Assert.Equal("c1", turn.ConversationId);
        Assert.Equal("server-system", turn.SystemPrompt);
        Assert.Equal(2, turn.MaxToolRounds);
        Assert.Equal("trace-1", turn.CorrelationId);
        var history = Assert.Single(turn.History);
        Assert.Equal("previous", history.Content);

        Assert.Single(result.Budget.Settlements);
        Assert.True(result.Budget.Settlements[0].Completed);
        Assert.Equal(2, result.Audit.Events.Count);
        Assert.True(result.Audit.Events[1].Success);
    }

    [Fact]
    public async Task MapAgentHarness_ForeignConversation_ReturnsForbiddenWithoutRunningEngine()
    {
        var store = new InMemoryConversationStore();
        await store.SaveUserMessageAsync(
            "tenant-1",
            "user-1",
            "c1",
            "secret",
            "correlation");

        var engine = new CapturingEchoEngine();
        var result = await InvokeAsync(engine, store, userId: "user-2");

        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Contains("无权访问", result.Body, StringComparison.Ordinal);
        Assert.Null(engine.Request);
        Assert.Empty(result.Budget.Settlements);
        Assert.Empty(result.Audit.Events);
    }

    [Fact]
    public async Task MapAgentHarness_WhenEngineFails_EmitsErrorAndSettlesIncomplete()
    {
        var result = await InvokeAsync(new ThrowingEngine());

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Contains("event: delta", result.Body, StringComparison.Ordinal);
        Assert.Contains("event: error", result.Body, StringComparison.Ordinal);
        Assert.Contains("rate_limited", result.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("event: done", result.Body, StringComparison.Ordinal);

        Assert.Single(result.Budget.Settlements);
        Assert.False(result.Budget.Settlements[0].Completed);
        Assert.Equal(2, result.Audit.Events.Count);
        Assert.False(result.Audit.Events[1].Success);
    }

    private static async Task<EndpointResult> InvokeAsync(
        IAgentEngine engine,
        IConversationStore? conversationStore = null,
        string userId = "user-1",
        string tenantId = "tenant-1")
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAgentHarnessCore();
        builder.Services.AddAgentHarnessAspNetCore(options =>
        {
            options.SystemPrompt = "server-system";
            options.MaxToolRounds = 2;
        });
        builder.Services.AddSingleton(engine);
        if (conversationStore is not null)
        {
            builder.Services.AddSingleton<IConversationStore>(conversationStore);
        }

        builder.Services.AddScoped<ITurnBudget, InMemoryTurnBudget>();
        builder.Services.AddScoped<IAuditSink, InMemoryAuditSink>();
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

        await using var app = builder.Build();
        app.MapAgentHarness("/agent/chat");

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(route => route.RoutePattern.RawText == "/agent/chat");

        var requestDelegate = endpoint.RequestDelegate;
        Assert.NotNull(requestDelegate);

        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim("tenant_id", tenantId),
            ], "test")),
            TraceIdentifier = "trace-1",
        };

        context.Request.Method = "POST";
        context.Request.Path = "/agent/chat";
        context.Request.ContentType = "application/json; charset=utf-8";
        var payload = Encoding.UTF8.GetBytes(
            """{"Input":"hi","ConversationId":"c1","TenantId":"evil","UserId":"evil"}""");
        context.Request.Body = new MemoryStream(payload);
        context.Request.ContentLength = payload.Length;
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetectionFeature(true));
        context.Response.Body = new MemoryStream();

        await requestDelegate(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();

        var budget = Assert.IsType<InMemoryTurnBudget>(
            scope.ServiceProvider.GetRequiredService<ITurnBudget>());
        var audit = Assert.IsType<InMemoryAuditSink>(
            scope.ServiceProvider.GetRequiredService<IAuditSink>());

        return new EndpointResult(context.Response.StatusCode, body, budget, audit);
    }

    private sealed record EndpointResult(
        int StatusCode,
        string Body,
        InMemoryTurnBudget Budget,
        InMemoryAuditSink Audit);

    private sealed class ThrowingEngine : IAgentEngine
    {
        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            AgentTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentEvent.TextDelta("partial");
            await Task.Yield();
            throw new ChatModelException(
                "rate_limited",
                "模型请求过于频繁，请稍后重试。",
                isTransient: true);
        }
    }

    private sealed class CapturingEchoEngine : IAgentEngine
    {
        public AgentTurnRequest? Request { get; private set; }

        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            AgentTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Request = request;
            var text = $"Echo: {request.Input}";
            yield return new AgentEvent.TextDelta(text);
            await Task.Yield();
            yield return new AgentEvent.Completed(new AgentTurnResult(
                text,
                "capture-model",
                AgentUsage.Empty,
                [],
                [],
                "stop"));
        }
    }

    private sealed class BodyDetectionFeature(bool canHaveBody) : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody { get; } = canHaveBody;
    }
}
