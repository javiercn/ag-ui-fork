using System.ComponentModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;

namespace Step09_InterruptsApproval.Server;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAGUI();

        // Register the delete_file tool wrapped with ApprovalRequiredAIFunction
        builder.Services.AddSingleton<AITool>(
            new ApprovalRequiredAIFunction(
                AIFunctionFactory.Create(DeleteFile, "delete_file", "Deletes a file from the system")));

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
                .UseFunctionInvocation();
        }
        else
        {
            builder.Services.AddSingleton<FakeChatClient>();
            builder.Services.AddChatClient(sp => sp.GetRequiredService<FakeChatClient>())
                .UseFunctionInvocation();
        }

        var app = builder.Build();

        app.MapAGUI("/");

        app.Run();
    }

    [Description("The filename to delete")]
    private static string DeleteFile(string filename)
    {
        return $"File '{filename}' deleted successfully.";
    }
}
