using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Vinx.Erp.Foundation;

public static class FoundationHealthEndpoints
{
    public static IEndpointRouteBuilder MapFoundationHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", () => Results.Text("Healthy"));
        return endpoints;
    }
}
