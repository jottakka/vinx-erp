using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.McpHost;

var builder = WebApplication.CreateBuilder(args);
const string McpRateLimitPolicy = "mcp";
builder.Logging.AddVinxLogging();
builder.Services.AddOpenTelemetry();
builder.Services.AddHealthChecks();
builder.Services.AddSupabaseJwtAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(McpRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.Identity?.IsAuthenticated == true ? "authenticated" : "anonymous",
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
app.MapHealthChecks("/health");
app.MapMcp("/mcp")
    .RequireAuthorization()
    .RequireRateLimiting(McpRateLimitPolicy);
app.Run();

public partial class Program;
