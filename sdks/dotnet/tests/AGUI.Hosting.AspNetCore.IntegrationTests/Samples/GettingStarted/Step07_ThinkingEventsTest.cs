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
using Step07_ThinkingEvents.Client;
using Step07_ThinkingEvents.Server;
using VerifyXunit;
using Xunit;

namespace AGUI.Hosting.AspNetCore.IntegrationTests.Samples.GettingStarted;

public sealed class Step07_ThinkingEventsTest : IntegrationTestBase<Step07_ThinkingEvents.Server.Program>
{
    public Step07_ThinkingEventsTest(WebApplicationFactory<Step07_ThinkingEvents.Server.Program> factory)
        : base(factory)
    {
    }

    [Fact]
    public async Task PostRun_WithReasoningContent_EmitsReasoningAndTextEvents()
    {
        var (aguiClient, transport, server) = CreateCapturingClient();

        var clientMessages = new List<List<ChatMessage>>();
        var clientUpdates = new List<List<ChatResponseUpdate>>();

        await Step07_ThinkingEvents.Client.SampleClient.RunAsync(
            aguiClient, TextWriter.Null, clientMessages, clientUpdates);

        await VerifyAllCaptures(transport, server, clientMessages, clientUpdates);
    }

    private (AGUIChatClient Client, CapturingAGUITransport Transport, CapturingChatClient Server) CreateCapturingClient(
        [CallerMemberName] string testName = "")
    {
        var serverCapture = new CapturingChatClient();

        // Create FakeChatClient with programmatic reasoning + text response
        var fakeClient = new FakeChatClient();
        fakeClient.Enqueue(_ => EmitReasoningAndTextResponse());

        serverCapture.SetInner(fakeClient);

        var factory = Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
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
    private static async IAsyncEnumerable<ChatResponseUpdate> EmitReasoningAndTextResponse()
    {
        // Reasoning content: thinking about the calculation
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextReasoningContent("Let me calculate 15 * 23. I can break this down: 15 * 20 = 300, 15 * 3 = 45, so 300 + 45 = 345.")]
        };

        // Text response with the answer
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            MessageId = "msg_calc_001",
            Contents = [new TextContent("15 × 23 = 345")]
        };
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
                }
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
