using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.McpHost;

var builder = WebApplication.CreateBuilder(args);
const string McpRateLimitPolicy = "mcp";
// Sem dependência de banco: readiness não pode exigir Postgres.
builder.AddVinxHostDefaults(registerPostgresHealthCheck: false);
builder.Services.AddSupabaseJwtAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(McpRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            // Partição por subject (sub), espelhando o ApiHost: um bucket
            // único "authenticated" deixaria todos os usuários disputando a
            // mesma janela.
            context.User.Identity?.IsAuthenticated == true
                ? context.User.FindFirst("sub")?.Value is { Length: > 0 } subject
                    ? $"authenticated:{subject}"
                    : "authenticated-without-sub"
                : "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }));
});
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<VinxPingTools>();

var app = builder.Build();
app.UseVinxRequestCorrelation();
app.UseRouting();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp")
        && context.Request.Headers.ContainsKey(HeaderNames.Origin))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await next(context);
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
// Liveness nunca executa checks: queda de banco não pode derrubar
// reinício do pod (readiness existe para isso).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions { Predicate = check => check.Tags.Contains("postgres") }
);
// Alias do probe de liveness: sem execução de checks, o /health histórico
// permanece 200 mesmo sem banco configurado.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapMcp("/mcp")
    .RequireAuthorization()
    .RequireRateLimiting(McpRateLimitPolicy);
app.Run();
