using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;

namespace Step04_HumanInLoop.Server;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAGUI();

        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Add(SampleJsonSerializerContext.Default));

        var approveExpenseReport = new ApprovalRequiredAIFunction(
            AIFunctionFactory.Create(
                BackendTools.ApproveExpenseReport,
                serializerOptions: SampleJsonSerializerContext.Default.Options));

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
                .Use((inner, _) => new ApprovalChatClient(inner, SampleJsonSerializerContext.Default.Options))
                .ConfigureOptions(options =>
                {
                    options.Tools ??= [];
                    options.Tools.Add(approveExpenseReport);
                });
        }
        else
        {
            builder.Services.AddSingleton<FakeChatClient>();
            builder.Services.AddChatClient(sp => sp.GetRequiredService<FakeChatClient>())
                .UseFunctionInvocation(configure: fic => fic.TerminateOnUnknownCalls = true)
                .Use((inner, _) => new ApprovalChatClient(inner, SampleJsonSerializerContext.Default.Options))
                .ConfigureOptions(options =>
                {
                    options.Tools ??= [];
                    options.Tools.Add(approveExpenseReport);
                });
        }

        var app = builder.Build();

        app.MapAGUI("/");

        app.Run();
    }
}
