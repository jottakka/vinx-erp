using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Vinx.Erp.ApiHost.Constants;
using Vinx.Erp.ApiHost.Identity;
using Vinx.Erp.ApiHost.OpenApi;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;

namespace Vinx.Erp.ApiHost.Agent;

/// <summary>
/// Superfície HTTP autenticada do agente: POST /agent/runs com parse estrito
/// do corpo (cap por bytes efetivamente lidos antes do parser síncrono),
/// resposta tipada do contrato do agente nas falhas de validação e envelope
/// {"code"} somente nas 5xx que não cabem no contrato. Nenhuma resposta é
/// cacheável (no-store).
/// </summary>
internal static class AgentEndpoints
{
    private const string InvalidRequest = "invalid_request";

    /// <summary>Mapeia o grupo /agent com autorização e rate limit das rotas protegidas.</summary>
    public static IEndpointRouteBuilder MapAgentRunSurface(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var agent = app
            .MapGroup("/agent")
            .RequireAuthorization()
            .RequireRateLimiting(ApiConstants.ProtectedRateLimitPolicy);
        agent
            .MapPost("/runs", RunAsync)
            .WithName("AgentRun")
            .WithMetadata(new JsonRequestBodyContract(typeof(AgentRunHttpRequest)))
            .WithMetadata(new BrowserBffNoStoreEndpointMetadata())
            .Produces<AgentRunHttpResponse>(StatusCodes.Status200OK)
            .Produces<AgentRunHttpResponse>(StatusCodes.Status400BadRequest)
            .Produces<AgentRunHttpResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status504GatewayTimeout);
        return app;
    }

    private static async Task<IResult> RunAsync(
        HttpRequest request,
        HttpContext httpContext,
        AgentExecutionApplicationService service,
        CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType())
        {
            return ValidationFailure(httpContext, InvalidRequest);
        }

        // O parser é síncrono: o corpo é bufferizado com cap por bytes
        // efetivamente lidos (inclusive transferência chunked) antes do TryParse.
        var buffer = new byte[ApiConstants.MaximumJsonBodyBytes + 1];
        var bodyLength = 0;
        try
        {
            int bytesRead;
            do
            {
                bytesRead = await request.Body.ReadAsync(buffer.AsMemory(bodyLength), cancellationToken);
                bodyLength += bytesRead;
            }
            while (bytesRead != 0 && bodyLength <= ApiConstants.MaximumJsonBodyBytes);
        }
        catch (IOException)
        {
            return ValidationFailure(httpContext, InvalidRequest);
        }

        if (bodyLength > ApiConstants.MaximumJsonBodyBytes)
        {
            return ValidationFailure(httpContext, InvalidRequest);
        }

        using var body = new MemoryStream(buffer, 0, bodyLength);
        if (
            !AgentHttpContract.TryParse(body, ApiConstants.MaximumJsonBodyBytes, out var agentRequest, out var errorCode)
            || agentRequest is null
        )
        {
            return ValidationFailure(httpContext, errorCode ?? InvalidRequest);
        }

        var result = await service.RunAsync(httpContext.GetRequiredActorContext(), agentRequest, cancellationToken);
        return result.Response is { } response
            ? Results.Json(response, statusCode: result.HttpStatusCode)
            : Results.Json(new { code = result.ErrorCode }, statusCode: result.HttpStatusCode);
    }

    // Falha de validação usa o corpo tipado do contrato do agente (não o
    // envelope {"code"}): carrega o reasonCode estável do parser e o
    // correlation do servidor; nenhum campo do payload é ecoado.
    private static IResult ValidationFailure(HttpContext httpContext, string reasonCode) =>
        Results.Json(
            new AgentRunHttpResponse(
                AgentRunStatus.ValidationError,
                reasonCode,
                Text: null,
                Sources: null,
                ServerCorrelationContext.Get(httpContext).CorrelationId),
            statusCode: StatusCodes.Status400BadRequest);
}
