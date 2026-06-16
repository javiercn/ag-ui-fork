using AGUI.Abstractions;
using AGUI.Client;
using AGUI.Hosting.AspNetCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Step01_GettingStarted.Client;
using Step01_GettingStarted.Server;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using VerifyXunit;
using Xunit;

namespace AGUI.Hosting.AspNetCore.IntegrationTests.Samples.GettingStarted;

public sealed class Step01_GettingStartedTest : IntegrationTestBase<Step01_GettingStarted.Server.Program>
{
    public Step01_GettingStartedTest(WebApplicationFactory<Step01_GettingStarted.Server.Program> factory)
        : base(factory)
    {
    }

    [Fact]
    public async Task PostRun_MultiTurn_SynthesizesAssistantMessages()
    {
        var (aguiClient, transport, server) = CreateCapturingClient(turnCount: 2);

        var clientMessages = new List<List<ChatMessage>>();
        var clientUpdates = new List<List<ChatResponseUpdate>>();

        await SampleClient.RunAsync(aguiClient, TextWriter.Null, clientMessages, clientUpdates);

        await VerifyAllCaptures(transport, server, clientMessages, clientUpdates);
    }

    private (AGUIChatClient Client, CapturingAGUITransport Transport, CapturingChatClient Server) CreateCapturingClient(
        int turnCount = 1,
        [CallerMemberName] string testName = "")
    {
        var serverCapture = new CapturingChatClient();
        var recording = LoadRecording(testName);
        var hasRecording = recording.Count > 0 && recording[0].Count > 0;

        var httpClient = Factory.WithWebHostBuilder(builder =>
        {
            if (hasRecording)
            {
                // Replay mode: use FakeChatClient with recorded responses
                builder.ConfigureServices(services =>
                {
                    var chatClient = new FakeChatClient();
                    for (int i = 0; i < turnCount; i++)
                    {
                        if (i < recording.Count)
                        {
                            var turnUpdates = recording[i];
                            chatClient.Enqueue(_ => ReplayUpdates(turnUpdates));
                        }
                    }

                    services.AddSingleton(chatClient);
                });
            }

            builder.ConfigureTestServices(services =>
            {
                if (hasRecording)
                {
                    // Replay mode: wrap FakeChatClient
                    services.AddSingleton<IChatClient>(sp =>
                    {
                        var fake = sp.GetRequiredService<FakeChatClient>();
                        serverCapture.SetInner(fake);
                        return serverCapture;
                    });
                }
                else
                {
                    // Record mode: wrap whatever IChatClient the app registered (e.g. Azure OpenAI)
                    var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IChatClient));
                    if (descriptor != null)
                    {
                        services.Remove(descriptor);
                    }

                    services.AddSingleton<IChatClient>(sp =>
                    {
                        var inner = (IChatClient)descriptor!.ImplementationFactory!(sp);
                        serverCapture.SetInner(inner);
                        return serverCapture;
                    });
                }
            });
        }).CreateClient();

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
            $"Step01_GettingStartedTest.{testName}.recording.json");
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

        // Chain AIJsonUtilities first for MEAI types (ChatMessage, ChatResponseUpdate, etc.)
        // then AGUIJsonSerializerContext for AG-UI types (RunAgentInput, BaseEvent, etc.)
        options.TypeInfoResolverChain.Add(AIJsonUtilities.DefaultOptions.TypeInfoResolver!);
        options.TypeInfoResolverChain.Add(AGUIJsonSerializerContext.Default);
        AGUIServiceCollectionExtensions.RegisterInterruptContentTypes(options);

        return options;
    }

    // Captures all 8 data points across the 4 MEAI↔AG-UI boundaries per turn,
    // structured as { client: { ... }, server: { ... } }:
    //
    // Client side:
    //   chatMessages       → MEAI messages passed to AGUIChatClient
    //   runAgentInput      → AG-UI request serialized by client transport
    //   events             → AG-UI events deserialized by client transport
    //   chatResponseUpdates → MEAI updates returned from AGUIChatClient
    //
    // Server side:
    //   runAgentInput       → AG-UI request deserialized by server endpoint
    //   chatMessages        → MEAI messages passed to LLM (after AG-UI→MEAI conversion)
    //   chatResponseUpdates → MEAI updates returned by LLM
    //   events              → AG-UI events serialized by server endpoint (same as client.events over wire)
    private async Task VerifyAllCaptures(
        CapturingAGUITransport transport,
        CapturingChatClient server,
        List<List<ChatMessage>> clientMessages,
        List<List<ChatResponseUpdate>> clientUpdates,
        [CallerMemberName] string testName = "")
    {
        // Save the server responses as a recording for future replay
        SaveRecording(testName, server);

        var turns = new List<object>();
        for (int i = 0; i < transport.Turns.Count; i++)
        {
            var wire = transport.Turns[i];
            var srv = i < server.Calls.Count ? server.Calls[i] : null;

            // Derive the AG-UI events the server would emit from its captured ChatResponseUpdates
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
        await Verifier.VerifyJson(json)
            .ScrubMember("createdAt")
            .ScrubMember("totalTokenCount")
            .AddScrubber(builder =>
            {
                var text = builder.ToString();
                builder.Clear();
                builder.Append(Regex.Replace(
                    text,
                    @"(?<![a-zA-Z_])(chatcmpl-|thread_|run_)[A-Za-z0-9]+",
                    m =>
                    {
                        var prefix = m.Groups[1].Value;
                        var suffix = m.Value[prefix.Length..];
                        var (map, label) = prefix switch
                        {
                            "chatcmpl-" => (chatcmplMap, "chatcmpl-Id"),
                            "thread_" => (threadMap, "thread_Id"),
                            "run_" => (runMap, "run_Id"),
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
