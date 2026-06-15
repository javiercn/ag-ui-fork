using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AGUI.Abstractions;
using AGUI.Client;
using AGUI.Hosting.AspNetCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Step04_HumanInLoop;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using VerifyXunit;
using Xunit;

namespace AGUI.Hosting.AspNetCore.IntegrationTests.Samples.GettingStarted;

public sealed class Step04_HumanInLoopTest : IntegrationTestBase<Step04_HumanInLoop.Program>
{
    public Step04_HumanInLoopTest(WebApplicationFactory<Step04_HumanInLoop.Program> factory)
        : base(factory)
    {
    }

    [Description("Handle an approval request by automatically approving it.")]
    private static ApprovalResponse RequestApproval(
        [Description("The approval request to process")] ApprovalRequest request)
    {
        // Auto-approve for testing purposes
        return new ApprovalResponse
        {
            ApprovalId = request.ApprovalId,
            Approved = true
        };
    }

    [Fact]
    public async Task PostRun_WithApprovalRequired_ApprovesAndExecutesTool()
    {
        // Define client tool: request_approval (auto-approves)
        AITool[] clientTools = [AIFunctionFactory.Create(RequestApproval, serializerOptions: s_jsonOptions)];

        var (aguiClient, transport, server) = CreateCapturingClient(turnCount: 2);

        var clientMessages = new List<List<ChatMessage>>();
        var clientUpdates = new List<List<ChatResponseUpdate>>();

        // Single call from client's perspective: AGUIChatClient handles the approval loop internally
        // 1. Server returns request_approval tool call (wrapping ApproveExpenseReport)
        // 2. Client auto-approves via FunctionInvokingChatClient
        // 3. Server processes approval, executes tool, calls LLM
        // 4. LLM returns confirmation text
        var messages = new List<ChatMessage> { new(ChatRole.User, "Please approve expense report EXP-2024-001") };
        var options = new ChatOptions { Tools = clientTools.ToList<AITool>() };
        clientMessages.Add(messages.ToList());
        var updates = await CollectUpdates(aguiClient, messages, options);
        clientUpdates.Add(updates);

        await VerifyAllCaptures(transport, server, clientMessages, clientUpdates);
    }

    private (AGUIChatClient Client, CapturingAGUITransport Transport, CapturingChatClient Server) CreateCapturingClient(
        int turnCount = 1,
        [CallerMemberName] string testName = "")
    {
        var serverCapture = new CapturingChatClient();
        var recording = LoadRecording(testName);
        var hasRecording = recording.Count > 0 && recording[0].Count > 0;

        // Pre-create FakeChatClient with recorded handlers.
        var fakeClient = new FakeChatClient();
        if (hasRecording)
        {
            for (int i = 0; i < recording.Count; i++)
            {
                var callUpdates = recording[i];
                fakeClient.Enqueue(_ => ReplayUpdates(callUpdates));
            }
        }

        serverCapture.SetInner(fakeClient);

        var factory = Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                // Remove ALL IChatClient registrations.
                // Step04 does NOT use FunctionInvokingChatClient on the server
                // (approval-required tools are handled manually by the endpoint).
                services.RemoveAll<IChatClient>();
                services.AddSingleton<IChatClient>(serverCapture);
            });
        });

        var httpClient = factory.CreateClient();

        var transport = new AGUIHttpTransport(httpClient, "/");
        var transportCapture = new CapturingAGUITransport(transport);
        var aguiClient = new AGUIChatClient(transportCapture);

        return (aguiClient, transportCapture, serverCapture);
    }

#pragma warning disable CS1998 // Async method lacks 'await' operators
    private static async IAsyncEnumerable<ChatResponseUpdate> ReplayUpdates(
        List<ChatResponseUpdate> updates)
    {
        foreach (var update in updates)
        {
            yield return update;
        }
    }
#pragma warning restore CS1998

    private static string GetRecordingPath(string testName)
    {
        return Path.Combine(
            AttributeReader.GetProjectDirectory(),
            "Samples",
            "GettingStarted",
            $"Step04_HumanInLoopTest.{testName}.recording.json");
    }

    private static List<List<ChatResponseUpdate>> LoadRecording(string testName)
    {
        var path = GetRecordingPath(testName);
        if (!File.Exists(path))
        {
            return [];
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<List<ChatResponseUpdate>>>(json, s_jsonOptions) ?? [];
    }

    private static void SaveRecording(string testName, CapturingChatClient server)
    {
        var path = GetRecordingPath(testName);
        var turns = server.Calls.Select(c => c.Updates).ToList();
        var json = JsonSerializer.Serialize(turns, s_jsonOptions);
        File.WriteAllText(path, json);
    }

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
        options.TypeInfoResolverChain.Add(SampleJsonSerializerContext.Default);        AGUIServiceCollectionExtensions.RegisterInterruptContentTypes(options);
        return options;
    }

    private async Task VerifyAllCaptures(
        CapturingAGUITransport transport,
        CapturingChatClient server,
        List<List<ChatMessage>> clientMessages,
        List<List<ChatResponseUpdate>> clientUpdates,
        [CallerMemberName] string testName = "")
    {
        SaveRecording(testName, server);

        var turns = new List<object>();
        for (int i = 0; i < transport.Turns.Count; i++)
        {
            var wire = transport.Turns[i];
            var srv = i < server.Calls.Count ? server.Calls[i] : null;

            List<BaseEvent>? serverDerivedEvents = null;
            if (srv != null)
            {
                serverDerivedEvents = new List<BaseEvent>();
                await foreach (var evt in ReplayUpdates(srv.Updates)
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
        await Verifier.VerifyJson(json)
            .ScrubMember("createdAt")
            .ScrubMember("totalTokenCount")
            .AddScrubber(builder =>
            {
                var text = builder.ToString();
                builder.Clear();
                builder.Append(Regex.Replace(
                    text,
                    @"(?<![a-zA-Z_])(chatcmpl-|thread_|run_|call_|msg_)[A-Za-z0-9_]+",
                    m =>
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
                            _ => throw new InvalidOperationException()
                        };
                        if (!map.TryGetValue(suffix, out var index))
                        {
                            index = map.Count + 1;
                            map[suffix] = index;
                        }
                        return $"{label}_{index}";
                    }));
            });
    }
}
