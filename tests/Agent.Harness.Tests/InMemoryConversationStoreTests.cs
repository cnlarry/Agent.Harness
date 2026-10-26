using Agent.Harness.Abstractions;
using Agent.Harness.Testing;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class InMemoryConversationStoreTests
{
    [Fact]
    public async Task SaveAndLoad_IsIsolatedByTenantAndUser()
    {
        var store = new InMemoryConversationStore();
        await store.SaveUserMessageAsync("tenant-1", "user-1", "c1", "hello", "corr");
        await store.SaveAssistantMessageAsync(
            "tenant-1",
            "user-1",
            "c1",
            Result("world"),
            "corr");

        var own = await store.LoadAsync("tenant-1", "user-1", "c1", 10);
        Assert.Equal(2, own.Count);
        Assert.Equal("hello", own[0].Content);
        Assert.Equal("world", own[1].Content);

        var otherUser = await store.LoadAsync("tenant-1", "user-2", "c1", 10);
        Assert.Empty(otherUser);

        var otherTenant = await store.LoadAsync("tenant-2", "user-1", "c1", 10);
        Assert.Empty(otherTenant);
    }

    [Fact]
    public async Task IsOwnedByAsync_AllowsNewConversationAndRejectsOtherUser()
    {
        var store = new InMemoryConversationStore();

        Assert.True(await store.IsOwnedByAsync("tenant-1", "user-1", "c1"));

        await store.SaveUserMessageAsync("tenant-1", "user-1", "c1", "hello", "corr");

        Assert.True(await store.IsOwnedByAsync("tenant-1", "user-1", "c1"));
        Assert.False(await store.IsOwnedByAsync("tenant-1", "user-2", "c1"));
        Assert.True(await store.IsOwnedByAsync("tenant-2", "user-2", "c1"));
    }

    [Fact]
    public async Task SaveUserMessage_RejectsForeignUser()
    {
        var store = new InMemoryConversationStore();
        await store.SaveUserMessageAsync("tenant-1", "user-1", "c1", "hello", "corr");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.SaveUserMessageAsync(
                "tenant-1",
                "user-2",
                "c1",
                "intrusion",
                "corr"));
    }

    [Fact]
    public async Task LoadAsync_ReturnsNewestMessages()
    {
        var store = new InMemoryConversationStore();
        for (var index = 0; index < 5; index++)
        {
            await store.SaveUserMessageAsync(
                "tenant-1",
                "user-1",
                "c1",
                $"message-{index}",
                "corr");
        }

        var loaded = await store.LoadAsync("tenant-1", "user-1", "c1", 2);

        Assert.Equal(2, loaded.Count);
        Assert.Equal("message-3", loaded[0].Content);
        Assert.Equal("message-4", loaded[1].Content);
    }

    private static AgentTurnResult Result(string text) => new(
        Text: text,
        ModelName: "test",
        Usage: AgentUsage.Empty,
        ToolCalls: [],
        Drafts: [],
        FinishReason: "stop");
}
