using Agent.Harness.Abstractions;

namespace Agent.Harness.Core;

/// <summary>工具白名单。名称重复会 fail-fast，避免“同名工具静默覆盖”。</summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        _tools = tools.ToDictionary(
            tool => tool.Descriptor.Name,
            StringComparer.Ordinal);
    }

    public IReadOnlyList<ToolDescriptor> Definitions => _tools.Values
        .Select(tool => tool.Descriptor)
        .OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal)
        .ToArray();

    public bool TryGet(string name, out ITool tool)
    {
        if (_tools.TryGetValue(name, out var found))
        {
            tool = found;
            return true;
        }

        tool = null!;
        return false;
    }

    public ToolRisk RiskOf(string name) =>
        _tools.TryGetValue(name, out var tool) ? tool.Descriptor.Risk : ToolRisk.Read;

    public IReadOnlyList<ITool> Resolve(IReadOnlyCollection<string>? enabledNames = null)
    {
        if (enabledNames is null || enabledNames.Count == 0)
        {
            return _tools.Values.ToArray();
        }

        return _tools.Values
            .Where(tool => enabledNames.Contains(tool.Descriptor.Name, StringComparer.Ordinal))
            .ToArray();
    }
}
