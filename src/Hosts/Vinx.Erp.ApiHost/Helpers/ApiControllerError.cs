using Microsoft.AspNetCore.Mvc;

namespace Vinx.Erp.ApiHost.Helpers;

internal static class ApiControllerError
{
    public static IActionResult InvalidRequest(int statusCode) =>
        Error("invalid_request", statusCode);

    public static IActionResult Error(string code, int statusCode) =>
        new ObjectResult(new { code }) { StatusCode = statusCode };
}
