using System.Text.Json;
using Vinx.Erp.AI.AgentHarness;

namespace Vinx.Erp.AI.Tools;

public interface IAgentTool
{
    AgentToolDescriptor Descriptor { get; }

    AgentToolValidationResult Validate(AgentToolCall call);

    Task<AgentToolResult> InvokeAsync(
        AgentToolInvocation invocation,
        CancellationToken cancellationToken);
}

public sealed record AgentToolDescriptor
{
    public AgentToolDescriptor(
        string name,
        string version,
        JsonElement inputSchema,
        JsonElement outputSchema,
        string requiredCapability,
        AgentToolRisk risk,
        bool requiresExplicitCompany,
        bool requiresIdempotencyKey,
        AgentMcpAnnotations mcpAnnotations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredCapability);
        ArgumentNullException.ThrowIfNull(mcpAnnotations);

        if (inputSchema.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("Input schema must be defined.", nameof(inputSchema));
        }

        if (outputSchema.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("Output schema must be defined.", nameof(outputSchema));
        }

        if (!Enum.IsDefined(risk))
        {
            throw new ArgumentOutOfRangeException(nameof(risk), risk, "Unsupported tool risk.");
        }

        Name = name;
        Version = version;
        InputSchema = inputSchema.Clone();
        OutputSchema = outputSchema.Clone();
        RequiredCapability = requiredCapability;
        Risk = risk;
        RequiresExplicitCompany = requiresExplicitCompany;
        RequiresIdempotencyKey = requiresIdempotencyKey;
        McpAnnotations = mcpAnnotations;
    }

    public string Name { get; }
    public string Version { get; }
    public JsonElement InputSchema { get; }
    public JsonElement OutputSchema { get; }
    public string RequiredCapability { get; }
    public AgentToolRisk Risk { get; }
    public bool RequiresExplicitCompany { get; }
    public bool RequiresIdempotencyKey { get; }
    public AgentMcpAnnotations McpAnnotations { get; }
}

public sealed record AgentToolCall
{
    public AgentToolCall(string name, JsonElement arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (arguments.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("Tool arguments must be defined.", nameof(arguments));
        }

        Name = name;
        Arguments = RemoveCompanyScopeArguments(arguments);
    }

    public string Name { get; }
    public JsonElement Arguments { get; }

    private static JsonElement RemoveCompanyScopeArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return arguments.Clone();
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            foreach (var property in arguments.EnumerateObject())
            {
                if (!IsCompanyScopeAlias(property.Name))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        using var normalized = JsonDocument.Parse(buffer.ToArray());
        return normalized.RootElement.Clone();
    }

    private static bool IsCompanyScopeAlias(string name) =>
        name.Equals("empresa_id", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("empresaId", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("company_id", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("companyId", StringComparison.OrdinalIgnoreCase);
}

public sealed record AgentToolInvocation
{
    public AgentToolInvocation(
        Guid runId,
        AgentExecutionContext executionContext,
        AgentToolCall call)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(runId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(call);

        RunId = runId;
        ExecutionContext = executionContext;
        Call = call;
    }

    public Guid RunId { get; }
    public AgentExecutionContext ExecutionContext { get; }
    public AgentToolCall Call { get; }
}

public sealed record AgentToolResult
{
    public AgentToolResult(
        string toolName,
        bool isSuccess,
        string safeCode,
        JsonElement output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeCode);

        if (output.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("Tool output must be defined.", nameof(output));
        }

        ToolName = toolName;
        IsSuccess = isSuccess;
        SafeCode = safeCode;
        Output = output.Clone();
    }

    public string ToolName { get; }
    public bool IsSuccess { get; }
    public string SafeCode { get; }
    public JsonElement Output { get; }
}

public sealed record AgentToolValidationResult
{
    public AgentToolValidationResult(bool isValid, string? safeCode)
    {
        if (isValid && safeCode is not null)
        {
            throw new ArgumentException("Valid results cannot carry an error code.", nameof(safeCode));
        }

        if (!isValid && string.IsNullOrWhiteSpace(safeCode))
        {
            throw new ArgumentException("Invalid results require a safe error code.", nameof(safeCode));
        }

        IsValid = isValid;
        SafeCode = safeCode;
    }

    public bool IsValid { get; }
    public string? SafeCode { get; }
}

public sealed record AgentMcpAnnotations(
    bool ReadOnlyHint,
    bool DestructiveHint,
    bool IdempotentHint,
    bool OpenWorldHint);

public enum AgentToolRisk
{
    ReadOnly,
    InternalWrite,
    ExternalWrite,
    Financial,
    Irreversible
}
