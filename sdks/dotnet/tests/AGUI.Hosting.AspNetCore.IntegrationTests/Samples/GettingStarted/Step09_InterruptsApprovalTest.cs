using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AGUI.Abstractions;
using AGUI.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Step09_InterruptsApproval.Client;
using Step09_InterruptsApproval.Server;
using VerifyXunit;
using Xunit;

namespace AGUI.Hosting.AspNetCore.IntegrationTests.Samples.GettingStarted;

public sealed class Step09_InterruptsApprovalTest : IntegrationTestBase<Step09_InterruptsApproval.Server.Program>
{
    public Step09_InterruptsApprovalTest(WebApplicationFactory<Step09_InterruptsApproval.Server.Program> factory)
        : base(factory)
    {
    }

    [Fact]
    public async Task PostRun_DeleteRequest_EmitsInterruptThenResumes()
    {
        var serverCapture = new CapturingChatClient();
        var fakeClient = new FakeChatClient();

        // Turn 1: FakeChatClient (acting as LLM) emits a FunctionCallContent for delete_file.
        // FunctionInvokingChatClient detects it requires approval (ApprovalRequiredAIFunction)
        // and converts it to ToolApprovalRequestContent.
        fakeClient.Enqueue(_ => EmitFunctionCallResponse(
            "call_delete1",
            "delete_file",
            new Dictionary<string, object?> { ["filename"] = "/etc/important.conf" }));

        // Turn 2: After FICC processes the approval and invokes delete_file,
        // it calls the FakeChatClient with the tool result in messages.
        // FakeChatClient returns the final success text.
        fakeClient.Enqueue(_ => EmitTextResponse(
            "The file '/etc/important.conf' has been deleted successfully."));

        serverCapture.SetInner(fakeClient);

        var factory = Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                // Replace the chat client pipeline with FunctionInvokingChatClient wrapping our captures
                services.RemoveAll<IChatClient>();
                services.AddChatClient(sp => (IChatClient)serverCapture)
                    .UseFunctionInvocation();
            });
        });

        var httpClient = factory.CreateClient();
        var transport = new AGUIHttpTransport(httpClient, "/");
        var transportCapture = new CapturingAGUITransport(transport);
        var aguiClient = new AGUIChatClient(transportCapture);

        var clientMessages = new List<List<ChatMessage>>();
        var clientUpdates = new List<List<ChatResponseUpdate>>();

        await Step09_InterruptsApproval.Client.SampleClient.RunAsync(
            aguiClient, TextWriter.Null, clientMessages, clientUpdates);

        await VerifyAllCaptures(transportCapture, serverCapture, clientMessages, clientUpdates);
    }

#pragma warning disable CS1998 // Async method lacks 'await' operators
    private static async IAsyncEnumerable<ChatResponseUpdate> EmitFunctionCallResponse(
        string callId,
        string functionName,
        IDictionary<string, object?> arguments)
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent(callId, functionName, arguments)],
            FinishReason = ChatFinishReason.ToolCalls
        };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmitTextResponse(string text)
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent(text)],
            MessageId = $"msg_{Guid.NewGuid():N}",
            ModelId = "gpt-4o"
        };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmitUpdates(List<ChatResponseUpdate> updates)
    {
        foreach (var update in updates)
        {
            yield return update;
        }
    }
#pragma warning restore CS1998

    private static readonly JsonSerializerOptions s_jsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        options.TypeInfoResolverChain.Add(AIJsonUtilities.DefaultOptions.TypeInfoResolver!);
        options.TypeInfoResolverChain.Add(AGUIJsonSerializerContext.Default);
        AGUIServiceCollectionExtensions.RegisterInterruptContentTypes(options);

        return options;
    }

    private async Task VerifyAllCaptures(
        CapturingAGUITransport transport,
        CapturingChatClient server,
        List<List<ChatMessage>> clientMessages,
        List<List<ChatResponseUpdate>> clientUpdates,
        [CallerMemberName] string testName = "")
    {
        var turns = new List<object>();
        for (int i = 0; i < transport.Turns.Count; i++)
        {
            var wire = transport.Turns[i];
            var srv = i < server.Calls.Count ? server.Calls[i] : null;

            List<BaseEvent>? serverDerivedEvents = null;
            if (srv != null)
            {
                serverDerivedEvents = new List<BaseEvent>();
                await foreach (var evt in EmitUpdates(srv.Updates)
                    .AsAGUIEventStreamAsync(wire.Input.ToChatRequestContext(s_jsonOptions)))
                {
                    serverDerivedEvents.Add(evt);
                }
            }

            turns.Add(new
            {
                client = new
                {
                    chatMessages = i < clientMessages.Count
                        ? clientMessages[i]
                        : null,
                    runAgentInput = wire.Input,
                    events = wire.Events,
                    chatResponseUpdates = i < clientUpdates.Count
                        ? clientUpdates[i]
                        : null
                },
                server = srv != null ? new
                {
                    runAgentInput = srv.RunAgentInput,
                    chatMessages = srv.Messages,
                    chatResponseUpdates = srv.Updates,
                    events = serverDerivedEvents
                } : null
            });
        }

        var json = JsonSerializer.Serialize(turns, s_jsonOptions);
        var chatcmplMap = new Dictionary<string, int>();
        var threadMap = new Dictionary<string, int>();
        var runMap = new Dictionary<string, int>();
        var toolCallIdMap = new Dictionary<string, int>();
        var msgIdMap = new Dictionary<string, int>();
        var guidMap = new Dictionary<string, int>();
        await Verifier.VerifyJson(json)
            .ScrubMember("createdAt")
            .ScrubMember("totalTokenCount")
            .AddScrubber(builder =>
            {
                var text = builder.ToString();
                builder.Clear();
                builder.Append(Regex.Replace(
                    text,
                    @"(?<![a-zA-Z_])(chatcmpl-|thread_|run_|call_|msg_|approval_|ficc_)[A-Za-z0-9_]+|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
                    m =>
                    {
                        if (m.Groups[1].Success)
                        {
                            var prefix = m.Groups[1].Value;
                            var suffix = m.Value[prefix.Length..];
                            var (map, label) = prefix switch
                            {
                                "chatcmpl-" => (chatcmplMap, "chatcmpl-Id"),
                                "thread_" => (threadMap, "thread_Id"),
                                "run_" => (runMap, "run_Id"),
                                "call_" => (toolCallIdMap, "call_Id"),
                                "msg_" => (msgIdMap, "msg_Id"),
                                "approval_" => (msgIdMap, "approval_Id"),
                                "ficc_" => (toolCallIdMap, "ficc_Id"),
                                _ => throw new InvalidOperationException()
                            };
                            if (!map.TryGetValue(suffix, out var index))
                            {
                                index = map.Count + 1;
                                map[suffix] = index;
                            }
                            return $"{label}_{index}";
                        }
                        else
                        {
                            // GUID pattern
                            var guidVal = m.Value;
                            if (!guidMap.TryGetValue(guidVal, out var index))
                            {
                                index = guidMap.Count + 1;
                                guidMap[guidVal] = index;
                            }
                            return $"Guid_{index}";
                        }
                    }));
            });
    }
}
