using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;

namespace Step06_RawEvents;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAGUI();

        builder.Services.AddSingleton<TelemetrySource>();

        if (string.Equals(builder.Configuration["UseAzureOpenAI"], "true", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = builder.Configuration["AZURE_OPENAI_ENDPOINT"]
                ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set.");
            var deploymentName = builder.Configuration["AZURE_OPENAI_DEPLOYMENT_NAME"]
                ?? throw new InvalidOperationException("AZURE_OPENAI_DEPLOYMENT_NAME is not set.");

            builder.Services.AddChatClient(new AzureOpenAIClient(
                    new Uri(endpoint),
                    new DefaultAzureCredential())
                .GetChatClient(deploymentName)
                .AsIChatClient())
                .UseFunctionInvocation(configure: fic => fic.TerminateOnUnknownCalls = true)
                .Use((inner, sp) => new TelemetryRawEventsChatClient(
                    inner,
                    sp.GetRequiredService<TelemetrySource>()));
        }
        else
        {
            builder.Services.AddSingleton<FakeChatClient>();
            builder.Services.AddChatClient(sp => sp.GetRequiredService<FakeChatClient>())
                .UseFunctionInvocation(configure: fic => fic.TerminateOnUnknownCalls = true)
                .Use((inner, sp) => new TelemetryRawEventsChatClient(
                    inner,
                    sp.GetRequiredService<TelemetrySource>()));
        }

        var app = builder.Build();

        app.MapAGUI("/");

        app.Run();
    }
}
