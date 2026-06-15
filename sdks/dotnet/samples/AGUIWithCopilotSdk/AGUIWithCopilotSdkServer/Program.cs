using AGUIWithCopilotSdkServer;
using GitHub.Copilot;

namespace AGUIWithCopilotSdkServer;

/// <summary>
/// Entry point for the AG-UI ↔ GitHub Copilot SDK sample. Defined in a
/// namespace so the auto-generated <c>Program</c> from top-level statements
/// doesn't collide with the integration test project's own <c>Program</c>
/// when this sample is added as a project reference.
/// </summary>
public sealed class Program
{
    public static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAGUI();
        builder.Services.AddLogging();

        // CopilotClient.StartAsync spawns the bundled GitHub Copilot CLI
        // runtime as a child process. This requires the runtime to be
        // available on the machine and the user to be authenticated (or a
        // GITHUB_TOKEN env var to be set). The sample registers the client
        // as a singleton — there's no benefit to per-request instantiation
        // because all sessions share the same runtime connection.
        builder.Services.AddSingleton<CopilotClient>(_ =>
        {
            CopilotClient client = new(new CopilotClientOptions
            {
                GitHubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
            });
            client.StartAsync().GetAwaiter().GetResult();
            return client;
        });

        builder.Services.AddSingleton<CopilotSessionRegistry>(sp =>
            new CopilotSessionRegistry(
                sp.GetRequiredService<CopilotClient>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<CopilotSessionRegistry>(),
                model: builder.Configuration["CopilotModel"]));

        WebApplication app = builder.Build();

        // Dispose the registry (which disposes every CopilotSession) and
        // then the underlying client on graceful shutdown so the runtime's
        // session state is flushed to disk.
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            CopilotSessionRegistry registry = app.Services.GetRequiredService<CopilotSessionRegistry>();
            registry.DisposeAsync().AsTask().GetAwaiter().GetResult();
            CopilotClient client = app.Services.GetRequiredService<CopilotClient>();
            client.StopAsync().GetAwaiter().GetResult();
        });

        app.MapCopilotAGUI("/agui");

        await app.RunAsync().ConfigureAwait(false);
    }
}
