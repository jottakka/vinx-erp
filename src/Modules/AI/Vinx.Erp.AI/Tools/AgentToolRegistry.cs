using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

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
        [NotNullWhen(true)] out IAgentTool? tool)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (TryGetRegistration(name, out var registration))
        {
            tool = registration.Tool;
            return true;
        }

        tool = null;
        return false;
    }

    internal bool TryGetRegistration(
        string name,
        [NotNullWhen(true)] out AgentToolRegistration? registration)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (registrations.TryGetValue(name, out registration) && registration.HasCurrentDescriptor)
        {
            return true;
        }

        registration = null;
        return false;
    }
}

internal sealed class AgentToolRegistration
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

    internal bool HasCurrentDescriptor
    {
        get
        {
            var current = Tool.Descriptor;

            return string.Equals(current.Name, Descriptor.Name, StringComparison.Ordinal)
                && string.Equals(current.Version, Descriptor.Version, StringComparison.Ordinal)
                && JsonElement.DeepEquals(current.InputSchema, Descriptor.InputSchema)
                && JsonElement.DeepEquals(current.OutputSchema, Descriptor.OutputSchema)
                && string.Equals(
                    current.RequiredCapability,
                    Descriptor.RequiredCapability,
                    StringComparison.Ordinal)
                && current.Risk == Descriptor.Risk
                && current.RequiresExplicitCompany == Descriptor.RequiresExplicitCompany
                && current.RequiresIdempotencyKey == Descriptor.RequiresIdempotencyKey
                && current.McpAnnotations == Descriptor.McpAnnotations;
        }
    }
}
