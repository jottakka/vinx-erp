using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.Foundation.Observability;

public sealed class RequestCorrelationMiddleware(RequestDelegate next, ILogger<RequestCorrelationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlation = ServerCorrelationContext.Begin(context);
        Activity.Current?.SetTag("vinx.correlation_id", correlation.CorrelationId.ToString());

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlation.CorrelationId
        });
        await next(context);
    }
}

public static class RequestCorrelationApplicationBuilderExtensions
{
    public static IApplicationBuilder UseVinxRequestCorrelation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<RequestCorrelationMiddleware>();
    }
}
