using AGUI.Samples.Shared;
using AGUI.Abstractions;
using AGUI.Server;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Step10_InterruptsUserInput.Server;

public sealed class Program
{
    internal const string ToolName = "request_user_input";

    private static readonly JsonElement ResponseSchema = JsonDocument.Parse(
        """
        {
          "type": "string"
        }
        """).RootElement.Clone();

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAGUI();

        IChatClient? azureChatClient = null;
        if (string.Equals(builder.Configuration["UseAzureOpenAI"], "true", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = builder.Configuration["AZURE_OPENAI_ENDPOINT"]
                ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set.");
            var deploymentName = builder.Configuration["AZURE_OPENAI_DEPLOYMENT_NAME"]
                ?? throw new InvalidOperationException("AZURE_OPENAI_DEPLOYMENT_NAME is not set.");

            azureChatClient = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
                .GetChatClient(deploymentName)
                .AsIChatClient();
        }
        else
        {
            builder.Services.AddSingleton<FakeChatClient>();
        }

        // The model is offered a normal function tool. Its call remains a FunctionCallContent
        // and the stream mapper additionally classifies the update as an AG-UI interruption.
        var requestUserInput = AIFunctionFactory.Create(
            (string prompt) => string.Empty,
            ToolName,
            "Ask the end user for a piece of free-form text. Pass the question to show them as 'prompt'.");

        builder.Services.AddChatClient(sp =>
                new InputRequestChatClient(
                    azureChatClient ?? sp.GetRequiredService<FakeChatClient>()))
            .ConfigureOptions(options =>
            {
                options.Instructions =
                    "You are an account-setup assistant. To finish creating the account you must know the " +
                    "user's preferred username. Use the request_user_input tool to ask the user for it, passing " +
                    "a short question as the 'prompt'. After the tool returns the username, confirm in one sentence " +
                    "that the account has been created.";
                options.Tools ??= [];
                options.Tools.Add(requestUserInput);
            });

        var app = builder.Build();

        app.MapAGUI(
            "/",
            new AGUIStreamOptions().MapInterrupt(update =>
            {
                if (update.RawRepresentation is not InputRequestEvent request)
                {
                    return null;
                }

                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent call &&
                        string.Equals(call.CallId, request.CallId, StringComparison.Ordinal))
                    {
                        return
                        [
                            new AGUIInterrupt
                            {
                                Id = call.CallId,
                                ToolCallId = call.CallId,
                                Reason = InterruptReasons.InputRequired,
                                Message = request.Message,
                                ResponseSchema = ResponseSchema,
                            },
                        ];
                    }
                }

                return null;
            }));

        app.Run();
    }

    internal static string ExtractPrompt(IDictionary<string, object?>? arguments)
    {
        if (arguments is not null && arguments.TryGetValue("prompt", out var value))
        {
            return value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element =>
                    element.GetString() ?? string.Empty,
                _ => value?.ToString() ?? string.Empty,
            };
        }

        return "Please provide the requested input.";
    }
}
