using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Vinx.Erp.AI.Tools;

public sealed class AgentToolRegistry
{
    private readonly FrozenDictionary<string, IAgentTool> tools;

    public AgentToolRegistry(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        this.tools = tools.ToFrozenDictionary(
            tool => tool.Descriptor.Name,
            StringComparer.Ordinal);

        Descriptors = Array.AsReadOnly(
            this.tools.Values
                .Select(tool => tool.Descriptor)
                .OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal)
                .ToArray());
    }

    public IReadOnlyList<AgentToolDescriptor> Descriptors { get; }

    public bool TryGet(
        string name,
        [NotNullWhen(true)] out IAgentTool? tool)
    {
        ArgumentNullException.ThrowIfNull(name);

        return tools.TryGetValue(name, out tool);
    }
}
