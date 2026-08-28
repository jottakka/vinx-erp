using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Vinx.Erp.McpHost;

internal sealed class VinxPingTools
{
    [McpServerTool(
        Name = "vinx_ping",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Confirms that the VINX MCP host is available without reading domain data.")]
    public static string Ping() => "pong";
}
