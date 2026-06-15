using AGUIWithMcpToolsServer;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace AGUIWithMcpToolsServer;

/// <summary>
/// Entry point for the AG-UI + MCP sample's host process.
///
/// Wiring:
/// <list type="number">
///   <item>Spawn the sibling <c>AGUIWithMcpToolsMcpServer</c> as a child
///         process and connect to it over stdio — the standard MCP
///         deployment shape. The MCP server's <c>[McpServerTool]</c>
///         methods are discovered remotely via <c>ListToolsAsync</c>.</item>
///   <item>Register the resulting <c>McpClient</c> + a cached tool list
///         (<see cref="McpToolProvider"/>) in DI.</item>
///   <item>Register an Azure OpenAI-backed <see cref="IChatClient"/> with
///         <c>UseFunctionInvocation</c> so the LLM can call any MCP tool
///         like a normal <c>AIFunction</c>.</item>
///   <item>Map the AG-UI endpoint at <c>/</c>.</item>
/// </list>
/// </summary>
public sealed class Program
{
    public static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAGUI();

        string mcpExePath = ResolveMcpServerExecutable();
        StdioClientTransport transport = new(new StdioClientTransportOptions
        {
            Name = "AGUIWithMcpToolsMcpServer",
            Command = mcpExePath,
        });

        McpClient mcpClient = await McpClient.CreateAsync(transport).ConfigureAwait(false);
        builder.Services.AddSingleton(mcpClient);
        builder.Services.AddSingleton<McpToolProvider>();

        string endpoint = builder.Configuration["AZURE_OPENAI_ENDPOINT"]
            ?? throw new InvalidOperationException(
                "AZURE_OPENAI_ENDPOINT must be set. See README for the gpt-5-mini setup used by the sample.");
        string deploymentName = builder.Configuration["AZURE_OPENAI_DEPLOYMENT_NAME"]
            ?? throw new InvalidOperationException(
                "AZURE_OPENAI_DEPLOYMENT_NAME must be set (e.g. \"gpt-5-mini\").");

        builder.Services.AddChatClient(new AzureOpenAIClient(
                new Uri(endpoint),
                new DefaultAzureCredential())
            .GetChatClient(deploymentName)
            .AsIChatClient())
            .UseFunctionInvocation();

        WebApplication app = builder.Build();

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            mcpClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });

        app.MapAGUI("/");

        await app.RunAsync().ConfigureAwait(false);
    }

    private static string ResolveMcpServerExecutable()
    {
        string thisDir = Path.GetDirectoryName(typeof(Program).Assembly.Location)
            ?? throw new InvalidOperationException("Cannot resolve host assembly directory.");
        string tfm = Path.GetFileName(thisDir);
        string config = Path.GetFileName(Path.GetDirectoryName(thisDir)!);
        string sampleRoot = Path.GetFullPath(Path.Combine(thisDir, "..", "..", "..", ".."));
        string mcpBin = Path.Combine(sampleRoot, "AGUIWithMcpToolsMcpServer", "bin", config, tfm);

        string exeName = OperatingSystem.IsWindows()
            ? "AGUIWithMcpToolsMcpServer.exe"
            : "AGUIWithMcpToolsMcpServer";
        string exePath = Path.Combine(mcpBin, exeName);

        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException(
                $"Could not find the MCP server executable at {exePath}. " +
                "Build the AGUIWithMcpToolsMcpServer project first (the solution build does this automatically).",
                exePath);
        }
        return exePath;
    }
}
