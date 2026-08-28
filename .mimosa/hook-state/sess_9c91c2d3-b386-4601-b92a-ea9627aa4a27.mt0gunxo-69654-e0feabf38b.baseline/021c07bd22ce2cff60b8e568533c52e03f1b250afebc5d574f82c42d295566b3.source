using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vinx.Erp.Foundation.Observability;

public static class VinxLoggingExtensions
{
    public static ILoggingBuilder AddVinxLogging(this ILoggingBuilder logging)
    {
        logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.TimestampFormat = "O";
            options.UseUtcTimestamp = true;
        });
        logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        logging.Services.Configure<LoggerFactoryOptions>(options =>
            options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

        return logging;
    }
}
