using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Vinx.Erp.ApiHost.Filters;

/// <summary>
/// Validação estrita de fronteira antes do binding: content-type JSON, limite de bytes
/// efetivamente lidos (inclusive chunked) e rewinding do corpo para o model binder.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class StrictJsonBodyAttribute : Attribute, IAsyncResourceFilter
{
    private readonly int _maximumBytes;

    public StrictJsonBodyAttribute(int maximumBytes)
    {
        _maximumBytes = maximumBytes;
    }

    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next
    )
    {
        var request = context.HttpContext.Request;
        var contentType = request.ContentType?.Split(';', 2)[0].Trim();
        if (!string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            context.Result = new ObjectResult(new { code = "invalid_request" })
            {
                StatusCode = StatusCodes.Status415UnsupportedMediaType,
            };
            return;
        }

        request.EnableBuffering();
        var total = 0L;
        var buffer = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(buffer)) > 0)
        {
            total += read;
            if (total > _maximumBytes)
            {
                context.Result = new ObjectResult(new { code = "invalid_request" })
                {
                    StatusCode = StatusCodes.Status400BadRequest,
                };
                return;
            }
        }

        request.Body.Seek(0, SeekOrigin.Begin);
        await next();
    }
}
