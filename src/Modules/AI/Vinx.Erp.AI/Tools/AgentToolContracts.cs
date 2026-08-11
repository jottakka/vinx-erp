using System.Text.Json;
using Vinx.Erp.AI.AgentHarness;

namespace Vinx.Erp.AI.Tools;

public enum AgentToolRisk
{
    ReadOnly = 1,
    InternalWrite = 2,
    ExternalWrite = 3
}

public interface IAgentTool
{
    AgentToolDescriptor Descriptor { get; }

    AgentToolValidationResult Validate(AgentToolCall call);

    Task<AgentToolResult> InvokeAsync(
        AgentToolInvocation invocation,
        CancellationToken cancellationToken);
}

public sealed record AgentToolMcpHints
{
    public AgentToolMcpHints(
        string? title,
        bool readOnlyHint,
        bool destructiveHint = false,
        bool idempotentHint = false,
        bool openWorldHint = false)
    {
        if (title is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
        }

        Title = title;
        ReadOnlyHint = readOnlyHint;
        DestructiveHint = destructiveHint;
        IdempotentHint = idempotentHint;
        OpenWorldHint = openWorldHint;
    }

    public string? Title { get; }
    public bool ReadOnlyHint { get; }
    public bool DestructiveHint { get; }
    public bool IdempotentHint { get; }
    public bool OpenWorldHint { get; }
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
        bool requiresIdempotency,
        AgentToolMcpHints mcpHints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredCapability);
        ArgumentNullException.ThrowIfNull(mcpHints);
        if (!Enum.IsDefined(risk)) throw new ArgumentOutOfRangeException(nameof(risk));

        Name = name;
        Version = version;
        InputSchema = inputSchema.Clone();
        OutputSchema = outputSchema.Clone();
        RequiredCapability = requiredCapability;
        Risk = risk;
        RequiresExplicitCompany = requiresExplicitCompany;
        RequiresIdempotency = requiresIdempotency;
        McpHints = mcpHints;
    }

    public string Name { get; }
    public string Version { get; }
    public JsonElement InputSchema { get; }
    public JsonElement OutputSchema { get; }
    public string RequiredCapability { get; }
    public AgentToolRisk Risk { get; }
    public bool RequiresExplicitCompany { get; }
    public bool RequiresIdempotency { get; }
    public AgentToolMcpHints McpHints { get; }

    internal AgentToolDescriptor Snapshot() => new(
        Name,
        Version,
        InputSchema,
        OutputSchema,
        RequiredCapability,
        Risk,
        RequiresExplicitCompany,
        RequiresIdempotency,
        McpHints);
}

public sealed record AgentToolCall
{
    public AgentToolCall(string name, JsonElement arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
        Arguments = Sanitize(arguments);
    }

    public string Name { get; }
    public JsonElement Arguments { get; }

    private static JsonElement Sanitize(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteSanitized(writer, value);
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteSanitized(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    if (IsCompanyAlias(property.Name)) continue;

                    writer.WritePropertyName(property.Name);
                    WriteSanitized(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteSanitized(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static bool IsCompanyAlias(string propertyName)
    {
        var normalized = propertyName.Replace("_", string.Empty, StringComparison.Ordinal);
        return normalized.Equals("empresaid", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("companyid", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record AgentToolValidationResult
{
    private AgentToolValidationResult(bool isValid, string? safeCode)
    {
        if (!isValid)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(safeCode);
        }

        IsValid = isValid;
        SafeCode = safeCode;
    }

    public bool IsValid { get; }
    public string? SafeCode { get; }

    public static AgentToolValidationResult Valid() => new(true, null);

    public static AgentToolValidationResult Invalid(string safeCode) => new(false, safeCode);
}

public sealed record AgentToolResult
{
    public AgentToolResult(string toolName, bool isSuccess, string safeCode, JsonElement payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeCode);

        ToolName = toolName;
        IsSuccess = isSuccess;
        SafeCode = safeCode;
        Payload = payload.Clone();
    }

    public string ToolName { get; }
    public bool IsSuccess { get; }
    public string SafeCode { get; }
    public JsonElement Payload { get; }
}

public sealed record AgentToolInvocation
{
    public AgentToolInvocation(Guid runId, AgentExecutionContext executionContext, AgentToolCall call)
    {
        if (runId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(runId));
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(call);

        RunId = runId;
        ExecutionContext = executionContext;
        Call = new AgentToolCall(call.Name, call.Arguments);
    }

    public Guid RunId { get; }
    public AgentExecutionContext ExecutionContext { get; }
    public AgentToolCall Call { get; }
}
