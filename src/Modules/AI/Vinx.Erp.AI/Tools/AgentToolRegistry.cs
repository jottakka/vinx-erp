using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Vinx.Erp.AI.Tools;

public sealed class AgentToolRegistry
{
    private readonly FrozenDictionary<string, AgentToolRegistration> registrations;

    public AgentToolRegistry(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var snapshots = tools
            .Select(tool =>
            {
                ArgumentNullException.ThrowIfNull(tool);
                return new AgentToolRegistration(tool, tool.Descriptor);
            })
            .ToArray();

        registrations = snapshots.ToFrozenDictionary(
            registration => registration.Descriptor.Name,
            StringComparer.Ordinal);

        Descriptors = Array.AsReadOnly(
            registrations.Values
                .Select(registration => registration.Descriptor)
                .OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal)
                .ToArray());
    }

    public IReadOnlyList<AgentToolDescriptor> Descriptors { get; }

    public bool TryGet(
        string name,
        [NotNullWhen(true)] out AgentToolRegistration? registration)
    {
        ArgumentNullException.ThrowIfNull(name);

        return registrations.TryGetValue(name, out registration);
    }
}

public sealed class AgentToolRegistration
{
    internal AgentToolRegistration(IAgentTool tool, AgentToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(descriptor);

        Tool = tool;
        Descriptor = descriptor;
    }

    public IAgentTool Tool { get; }
    public AgentToolDescriptor Descriptor { get; }
}
