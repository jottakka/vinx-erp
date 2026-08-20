using System.Text.Json;

namespace Vinx.Erp.ApiHost.Agent;

/// <summary>Status possíveis de uma execução de agente na fronteira HTTP.</summary>
public static class AgentRunStatus
{
    /// <summary>Execução concluída com o texto do modelo.</summary>
    public const string Result = "result";

    /// <summary>Execução pendente de esclarecimento do usuário.</summary>
    public const string NeedsClarification = "needs_clarification";

    /// <summary>Execução negada por política ou capacidade.</summary>
    public const string Denied = "denied";

    /// <summary>Entrada rejeitada pela validação server-side.</summary>
    public const string ValidationError = "validation_error";

    /// <summary>Conflito de execução (por exemplo, idempotência divergente).</summary>
    public const string Conflict = "conflict";

    /// <summary>Estado opaco aguardando confirmação explícita; nenhuma execução ocorreu.</summary>
    public const string RequiresConfirmation = "requires_confirmation";
}

/// <summary>Referência opaca a uma fonte autorizada citada na resposta.</summary>
public sealed record AgentRunSourceRef(string Reference);

/// <summary>Corpo aceito de POST /agent/runs; o cliente envia somente jornada, mensagem, requestId e empresa quando exigida.</summary>
public sealed record AgentRunHttpRequest(Guid RequestId, string Journey, string Message, Guid? CompanyId);

/// <summary>Resposta tipada de uma execução; texto somente o do modelo liberado pelo gate, sem prompt ou resposta bruta.</summary>
public sealed record AgentRunHttpResponse(
    string Status,
    string? ReasonCode,
    string? Text,
    IReadOnlyList<AgentRunSourceRef>? Sources,
    Guid CorrelationId
);

/// <summary>Parser estrito do corpo de POST /agent/runs: campos fechados, tipos exatos, sem exceções e sem eco do payload.</summary>
public static class AgentHttpContract
{
    /// <summary>Tamanho máximo da mensagem após trim.</summary>
    public const int MaxMessageLength = 4000;

    private const int MaxJourneyLength = 100;
    private const string InvalidRequest = "invalid_request";
    private const string RequestIdInvalid = "request_id_invalid";
    private const string JourneyInvalid = "journey_invalid";
    private const string MessageEmpty = "message_empty";
    private const string MessageTooLong = "message_too_long";

    private static readonly HashSet<string> Fields = ["requestId", "journey", "message", "companyId"];

    /// <summary>Interpreta o corpo com limite por bytes efetivamente lidos (inclusive transferência chunked); retorna false com código de erro estável quando viola o contrato.</summary>
    public static bool TryParse(Stream body, long maxBytes, out AgentRunHttpRequest? request, out string? errorCode)
    {
        request = null;
        errorCode = InvalidRequest;
        if (body is null || maxBytes is < 0 or > int.MaxValue - 1)
        {
            return false;
        }

        try
        {
            var buffer = new byte[maxBytes + 1];
            var bodyLength = 0;
            int bytesRead;
            do
            {
                bytesRead = body.Read(buffer, bodyLength, buffer.Length - bodyLength);
                bodyLength += bytesRead;
            } while (bytesRead != 0 && bodyLength <= maxBytes);

            if (bodyLength > maxBytes)
            {
                return false;
            }

            using var document = JsonDocument.Parse(buffer.AsMemory(0, bodyLength));
            return TryParse(document.RootElement, out request, out errorCode);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool TryParse(JsonElement root, out AgentRunHttpRequest? request, out string? errorCode)
    {
        request = null;
        errorCode = InvalidRequest;
        if (root.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!Fields.Contains(property.Name) || !values.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }

        if (
            !values.TryGetValue("requestId", out var requestIdElement)
            || !values.TryGetValue("journey", out var journeyElement)
            || !values.TryGetValue("message", out var messageElement)
        )
        {
            return false;
        }

        if (requestIdElement.ValueKind is not JsonValueKind.String || !requestIdElement.TryGetGuid(out var requestId) || requestId == Guid.Empty)
        {
            errorCode = RequestIdInvalid;
            return false;
        }

        if (journeyElement.ValueKind is not JsonValueKind.String)
        {
            errorCode = JourneyInvalid;
            return false;
        }

        var journey = journeyElement.GetString()!.Trim();
        if (journey.Length is < 1 or > MaxJourneyLength)
        {
            errorCode = JourneyInvalid;
            return false;
        }

        if (messageElement.ValueKind is not JsonValueKind.String)
        {
            return false;
        }

        var message = messageElement.GetString()!.Trim();
        if (message.Length == 0)
        {
            errorCode = MessageEmpty;
            return false;
        }

        if (message.Length > MaxMessageLength)
        {
            errorCode = MessageTooLong;
            return false;
        }

        Guid? companyId = null;
        if (
            values.TryGetValue("companyId", out var companyIdElement)
            && companyIdElement.ValueKind is not JsonValueKind.Null
        )
        {
            if (companyIdElement.ValueKind is not JsonValueKind.String || !companyIdElement.TryGetGuid(out var parsedCompanyId))
            {
                return false;
            }

            companyId = parsedCompanyId;
        }

        request = new AgentRunHttpRequest(requestId, journey, message, companyId);
        errorCode = null;
        return true;
    }
}
