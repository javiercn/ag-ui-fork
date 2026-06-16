using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AGUI.Abstractions;
using AGUI.Client;
using AGUI.Hosting.AspNetCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Step04_HumanInLoop.Client;
using Step04_HumanInLoop.Server;
using VerifyXunit;
using Xunit;

namespace AGUI.Hosting.AspNetCore.IntegrationTests.Samples.GettingStarted;

public sealed class Step04_HumanInLoopTest : IntegrationTestBase<Step04_HumanInLoop.Server.Program>
{
    public Step04_HumanInLoopTest(WebApplicationFactory<Step04_HumanInLoop.Server.Program> factory)
        : base(factory)
    {
    }

    [Fact]
    public async Task PostRun_WithApprovalRequired_RoundTripsThroughBothWrappers()
    {
        var (aguiClient, transport, server) = CreateCapturingClient();

        var clientMessages = new List<List<ChatMessage>>();
        var clientUpdates = new List<List<ChatResponseUpdate>>();

        await Step04_HumanInLoop.Client.SampleClient.RunAsync(
            aguiClient, TextWriter.Null, clientMessages, clientUpdates);

        await VerifyAllCaptures(transport, server, clientMessages, clientUpdates);
    }

    private (AGUIChatClient Client, CapturingAGUITransport Transport, CapturingChatClient Server) CreateCapturingClient(
        [CallerMemberName] string testName = "")
    {
        var serverCapture = new CapturingChatClient();
        var recording = LoadRecording(testName);
        var hasRecording = recording.Count > 0 && recording[0].Count > 0;

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
                services.RemoveAll<IChatClient>();
                services.AddSingleton<IChatClient>(sp =>
                {
                    return new ChatClientBuilder(serverCapture)
                        .ConfigureOptions(options =>
                        {
                            options.Tools ??= [];
                            options.Tools.Add(new ApprovalRequiredAIFunction(
                                AIFunctionFactory.Create(
                                    BackendTools.ApproveExpenseReport,
                                    serializerOptions: s_jsonOptions)));
                        })
                        .Use((inner, _) => new ApprovalChatClient(inner, s_jsonOptions))
                        .UseFunctionInvocation(configure: fic => fic.TerminateOnUnknownCalls = true)
                        .Build(sp);
                });
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
        options.TypeInfoResolverChain.Add(Step04_HumanInLoop.Server.SampleJsonSerializerContext.Default);
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
                    chatMessages = i < clientMessages.Count ? clientMessages[i] : null,
                    runAgentInput = wire.Input,
                    events = wire.Events,
                    chatResponseUpdates = i < clientUpdates.Count ? clientUpdates[i] : null,
                },
                server = srv != null ? new
                {
                    runAgentInput = srv.RunAgentInput,
                    chatMessages = srv.Messages,
                    chatResponseUpdates = srv.Updates,
                    events = serverDerivedEvents,
                } : null,
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
                            _ => throw new InvalidOperationException(),
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
