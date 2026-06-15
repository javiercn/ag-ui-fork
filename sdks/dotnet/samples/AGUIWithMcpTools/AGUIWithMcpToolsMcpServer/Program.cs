using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AGUIWithMcpToolsMcpServer;

/// <summary>
/// Entry point for the standalone stdio MCP server. Spawned as a child
/// process by AGUIWithMcpToolsServer (the AG-UI host) via the official
/// ModelContextProtocol C# SDK's <c>StdioClientTransport</c>.
///
/// All tool methods marked <c>[McpServerTool]</c> in this assembly are
/// auto-discovered by <c>WithToolsFromAssembly()</c>.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // Stdio MCP servers must NEVER log to stdout — that channel is the
        // JSON-RPC transport. Send everything to stderr instead so the host
        // process can capture it for diagnostics without corrupting the
        // protocol stream.
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync().ConfigureAwait(false);
    }
}
