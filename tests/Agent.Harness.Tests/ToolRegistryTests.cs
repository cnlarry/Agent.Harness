using Agent.Harness.Abstractions;
using Agent.Harness.Core;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class ToolRegistryTests
{
    [Fact]
    public void Definitions_AreSortedByName()
    {
        var registry = new ToolRegistry([new StubTool("b"), new StubTool("a")]);

        Assert.Equal(
            new[] { "a", "b" },
            registry.Definitions.Select(descriptor => descriptor.Name));
    }

    [Fact]
    public void TryGet_ReturnsRegisteredTool()
    {
        var tool = new StubTool("echo");
        var registry = new ToolRegistry([tool]);

        Assert.True(registry.TryGet("echo", out var found));
        Assert.Same(tool, found);
    }

    [Fact]
    public void TryGet_ReturnsFalseForUnknownTool()
    {
        var registry = new ToolRegistry([new StubTool("echo")]);

        Assert.False(registry.TryGet("missing", out _));
    }

    private sealed class StubTool(string name) : ITool
    {
        public ToolDescriptor Descriptor { get; } = new(
            name,
            name,
            """{"type":"object"}""",
            ToolRisk.Read);

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ToolResult.Success(name));
    }
}
