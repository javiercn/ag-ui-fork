using System.ComponentModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Step09_InterruptsApproval.Server;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAGUI();

        // delete_file is wrapped with ApprovalRequiredAIFunction so FunctionInvokingChatClient
        // produces ToolApprovalRequestContent (which the hosting layer renders as an
        // AG-UI RUN_FINISHED { outcome: interrupt }) instead of executing the function.
        var deleteFileTool = new ApprovalRequiredAIFunction(
            AIFunctionFactory.Create(DeleteFile, "delete_file", "Deletes a file from the system"));

        IChatClient inner;
        if (string.Equals(builder.Configuration["UseAzureOpenAI"], "true", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = builder.Configuration["AZURE_OPENAI_ENDPOINT"]
                ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set.");
            var deploymentName = builder.Configuration["AZURE_OPENAI_DEPLOYMENT_NAME"]
                ?? throw new InvalidOperationException("AZURE_OPENAI_DEPLOYMENT_NAME is not set.");

            inner = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
                .GetChatClient(deploymentName)
                .AsIChatClient();
        }
        else
        {
            builder.Services.AddSingleton<FakeChatClient>();
            inner = null!;
        }

        builder.Services.AddChatClient(sp => inner ?? sp.GetRequiredService<FakeChatClient>())
            .ConfigureOptions(options =>
            {
                options.Tools ??= [];
                options.Tools.Add(deleteFileTool);
            })
            .Use((c, sp) => new ToolApprovalResumeChatClient(
                c,
                sp.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions))
            .UseFunctionInvocation();

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
