using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Vinx.Erp.AI.Tools;

public sealed record AgentToolRegistration(IAgentTool Tool, AgentToolDescriptor Descriptor);

public sealed class AgentToolRegistry
{
    private readonly IReadOnlyDictionary<string, AgentToolRegistration> registrations;

    public AgentToolRegistry(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var registrationCopy = new Dictionary<string, AgentToolRegistration>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            ArgumentNullException.ThrowIfNull(tool.Descriptor);

            var descriptor = tool.Descriptor.Snapshot();
            if (!registrationCopy.TryAdd(descriptor.Name, new AgentToolRegistration(tool, descriptor)))
            {
                throw new ArgumentException("Only one active tool version is permitted for each name.", nameof(tools));
            }
        }

        registrations = new ReadOnlyDictionary<string, AgentToolRegistration>(registrationCopy);
        Descriptors = new ReadOnlyCollection<AgentToolDescriptor>(registrationCopy.Values
            .Select(registration => registration.Descriptor)
            .ToList());
    }

    public IReadOnlyList<AgentToolDescriptor> Descriptors { get; }

    public bool TryResolve(AgentToolCall call, [NotNullWhen(true)] out AgentToolRegistration? registration)
    {
        ArgumentNullException.ThrowIfNull(call);
        return registrations.TryGetValue(call.Name, out registration);
    }
}
