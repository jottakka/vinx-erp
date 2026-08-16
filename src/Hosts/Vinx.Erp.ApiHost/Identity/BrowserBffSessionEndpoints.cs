using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.ApiHost.Identity;

internal static class BrowserBffSessionEndpoints
{
    private const int MaximumSignInRequestBodyBytes = 8 * 1024;

    public static RouteGroupBuilder MapBrowserBffSessionEndpoints(this IEndpointRouteBuilder routes)
    {
        var browserSession = routes.MapGroup("/foundation/identity/browser-session");
        browserSession
            .MapGet(
                "/antiforgery",
                (HttpContext context, IAntiforgery antiforgery) =>
                {
                    var tokens = antiforgery.GetAndStoreTokens(context);
                    return string.IsNullOrWhiteSpace(tokens.RequestToken)
                        ? Results.Json(
                            new AuthorizationErrorResponse("identity_provider_unavailable"),
                            statusCode: StatusCodes.Status503ServiceUnavailable)
                        : Results.Ok(new BrowserBffAntiforgeryResponse(tokens.RequestToken));
                })
            .AllowAnonymous()
            .WithName("BrowserSessionAntiforgery")
            .Produces<BrowserBffAntiforgeryResponse>(StatusCodes.Status200OK)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status503ServiceUnavailable);
        browserSession
            .MapPost(
                "",
                async (
                    HttpRequest request,
                    HttpContext context,
                    BrowserBffSessionSignInService service,
                    CancellationToken cancellationToken) =>
                {
                    BrowserBffSessionSignInCommand command;
                    try
                    {
                        if (!request.HasJsonContentType())
                        {
                            return Results.Json(new { code = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
                        }

                        var body = new byte[MaximumSignInRequestBodyBytes + 1];
                        var bodyLength = 0;
                        int bytesRead;
                        do
                        {
                            bytesRead = await request.Body.ReadAsync(
                                body.AsMemory(bodyLength),
                                cancellationToken);
                            bodyLength += bytesRead;
                        }
                        while (bytesRead != 0 && bodyLength <= MaximumSignInRequestBodyBytes);

                        if (bodyLength > MaximumSignInRequestBodyBytes)
                        {
                            return Results.Json(new { code = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
                        }

                        using var document = JsonDocument.Parse(body.AsMemory(0, bodyLength));
                        if (!BrowserBffSessionHttpContract.TryParse(document.RootElement, out command))
                        {
                            return Results.Json(new { code = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
                        }
                    }
                    catch (JsonException)
                    {
                        return Results.Json(new { code = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
                    }
                    catch (IOException)
                    {
                        return Results.Json(new { code = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
                    }

                    var result = await service.SignInAsync(
                        command,
                        ServerCorrelationContext.Get(context).CorrelationId,
                        cancellationToken);
                    switch (result.State)
                    {
                        case BrowserBffSessionSignInState.Succeeded:
                            await context.SignInAsync(
                                BrowserBffAuthenticationExtensions.CookieScheme,
                                result.Principal!,
                                new AuthenticationProperties
                                {
                                    IsPersistent = false,
                                    AllowRefresh = false,
                                    ExpiresUtc = result.ExpiresUtc,
                                });
                            return Results.NoContent();
                        case BrowserBffSessionSignInState.Unauthorized:
                            return Results.Unauthorized();
                        case BrowserBffSessionSignInState.RateLimited:
                            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
                        default:
                            return Results.Json(
                                new AuthorizationErrorResponse("identity_provider_unavailable"),
                                statusCode: StatusCodes.Status503ServiceUnavailable);
                    }
                })
            .AllowAnonymous()
            .RequireRateLimiting(BrowserBffAuthenticationExtensions.LoginRateLimitPolicyName)
            .WithName("BrowserSessionSignIn")
            .WithMetadata(new BrowserBffAntiforgeryRequiredMetadata())
            .WithMetadata(new BrowserBffSessionSignInOpenApiMetadata())
            .Produces(StatusCodes.Status204NoContent)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status503ServiceUnavailable);
        browserSession
            .MapDelete(
                "",
                async (
                    HttpContext context,
                    SelfSessionRevocationService service,
                    CancellationToken cancellationToken) =>
                {
                    var result = await service.RevokeAllAsync(
                        context.GetRequiredActorContext(),
                        AuditSource.Web,
                        cancellationToken);
                    return result.State is SelfSessionRevocationState.Revoked or SelfSessionRevocationState.NoChange
                        ? Results.NoContent()
                        : Results.Json(
                            new AuthorizationErrorResponse("identity_gate_unavailable"),
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                })
            .WithName("BrowserSessionSignOut")
            .RequireAuthorization(BrowserBffAuthenticationExtensions.BrowserSessionPolicyName)
            .WithMetadata(new BrowserBffCookieOnlyEndpointMetadata())
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<AuthorizationErrorResponse>(StatusCodes.Status503ServiceUnavailable);
        return browserSession;
    }
}

internal sealed record BrowserBffAntiforgeryResponse(string RequestToken);

internal sealed record BrowserBffSessionSignInCommand(string Email, string Password);

internal static class BrowserBffSessionHttpContract
{
    private const int MaximumEmailLength = 320;
    private const int MaximumPasswordLength = 1024;
    private static readonly HashSet<string> Fields = ["email", "password"];

    public static bool TryParse(JsonElement body, out BrowserBffSessionSignInCommand command)
    {
        command = null!;
        if (body.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in body.EnumerateObject())
        {
            if (!Fields.Contains(property.Name) || !fields.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }

        if (fields.Count != Fields.Count
            || fields["email"].ValueKind is not JsonValueKind.String
            || fields["password"].ValueKind is not JsonValueKind.String)
        {
            return false;
        }

        var email = fields["email"].GetString()?.Trim();
        var password = fields["password"].GetString();
        if (string.IsNullOrWhiteSpace(email)
            || email.Length > MaximumEmailLength
            || string.IsNullOrEmpty(password)
            || password.Length > MaximumPasswordLength)
        {
            return false;
        }

        command = new BrowserBffSessionSignInCommand(email, password);
        return true;
    }
}
