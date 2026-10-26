using System.Security.Claims;
using Agent.Harness.Abstractions;
using Agent.Harness.AspNetCore;
using Agent.Harness.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class ClaimsAgentTurnFactoryTests
{
    [Fact]
    public async Task CreateAsync_UsesClaimsAndServerOptions()
    {
        var context = CreateContext();

        var factory = new ClaimsAgentTurnFactory(
            Options.Create(new AgentHarnessEndpointOptions
            {
                SystemPrompt = "server-system",
                MaxToolRounds = 3,
                HistoryLimit = 10,
            }),
            new ServiceCollection().BuildServiceProvider());

        var turn = await factory.CreateAsync(
            context,
            new AgentChatRequest("hello", "conversation-1"),
            CancellationToken.None);

        Assert.Equal("tenant-1", turn.TenantId);
        Assert.Equal("user-1", turn.UserId);
        Assert.Equal("conversation-1", turn.ConversationId);
        Assert.Equal("hello", turn.Input);
        Assert.Equal("server-system", turn.SystemPrompt);
        Assert.Equal(3, turn.MaxToolRounds);
        Assert.Equal("trace-1", turn.CorrelationId);
        Assert.Empty(turn.History);
    }

    [Fact]
    public async Task CreateAsync_WithoutUserClaim_ThrowsUnauthorized()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity()),
        };

        var factory = new ClaimsAgentTurnFactory(
            Options.Create(new AgentHarnessEndpointOptions()),
            new ServiceCollection().BuildServiceProvider());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await factory.CreateAsync(
                context,
                new AgentChatRequest("hello", "conversation-1"),
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_WithoutTenantClaim_ThrowsUnauthorized()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "user-1"),
            ], "test")),
        };

        var factory = new ClaimsAgentTurnFactory(
            Options.Create(new AgentHarnessEndpointOptions()),
            new ServiceCollection().BuildServiceProvider());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await factory.CreateAsync(
                context,
                new AgentChatRequest("hello", "conversation-1"),
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_ForeignConversation_ThrowsAccessDenied()
    {
        var store = new InMemoryConversationStore();
        await store.SaveUserMessageAsync(
            "tenant-1",
            "user-1",
            "conversation-1",
            "secret",
            "correlation");

        var context = CreateContext("user-2");
        var services = new ServiceCollection()
            .AddSingleton<IConversationStore>(store)
            .BuildServiceProvider();

        var factory = new ClaimsAgentTurnFactory(
            Options.Create(new AgentHarnessEndpointOptions()),
            services);

        await Assert.ThrowsAsync<ConversationAccessDeniedException>(async () =>
            await factory.CreateAsync(
                context,
                new AgentChatRequest("hello", "conversation-1"),
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_LoadsHistoryFromConversationStore()
    {
        var context = CreateContext();
        var store = new StubConversationStore();
        var services = new ServiceCollection()
            .AddSingleton<IConversationStore>(store)
            .BuildServiceProvider();

        var factory = new ClaimsAgentTurnFactory(
            Options.Create(new AgentHarnessEndpointOptions { HistoryLimit = 10 }),
            services);

        var turn = await factory.CreateAsync(
            context,
            new AgentChatRequest("hello", "conversation-1"),
            CancellationToken.None);

        var message = Assert.Single(turn.History);
        Assert.Equal("history", message.Content);
        Assert.Equal("tenant-1", store.TenantId);
        Assert.Equal("user-1", store.UserId);
        Assert.Equal("conversation-1", store.ConversationId);
        Assert.Equal(10, store.MaxMessages);
    }

    private static DefaultHttpContext CreateContext(string userId = "user-1")
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim("tenant_id", "tenant-1"),
            ], "test")),
            TraceIdentifier = "trace-1",
        };

        return context;
    }

    private sealed class StubConversationStore : IConversationStore
    {
        public string? TenantId { get; private set; }

        public string? UserId { get; private set; }

        public string? ConversationId { get; private set; }

        public int MaxMessages { get; private set; }

        public ValueTask<bool> IsOwnedByAsync(
            string tenantId,
            string userId,
            string conversationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask<IReadOnlyList<AgentMessage>> LoadAsync(
            string tenantId,
            string userId,
            string conversationId,
            int maxMessages,
            CancellationToken cancellationToken = default)
        {
            TenantId = tenantId;
            UserId = userId;
            ConversationId = conversationId;
            MaxMessages = maxMessages;

            IReadOnlyList<AgentMessage> history =
            [
                new AgentMessage(AgentRole.User, "history"),
            ];

            return ValueTask.FromResult(history);
        }

        public ValueTask SaveUserMessageAsync(
            string tenantId,
            string userId,
            string conversationId,
            string content,
            string correlationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SaveAssistantMessageAsync(
            string tenantId,
            string userId,
            string conversationId,
            AgentTurnResult result,
            string correlationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
