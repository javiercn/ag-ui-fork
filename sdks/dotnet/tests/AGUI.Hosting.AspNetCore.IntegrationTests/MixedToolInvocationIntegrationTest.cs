using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AGUI.Hosting.AspNetCore.IntegrationTests;

/// <summary>
/// Integration tests exercising the full mixed tool invocation two-turn flow:
/// Turn 1: LLM calls both server and client tools → FICC emits ToolApprovalRequestContent
///         → stream converter unwraps to TOOL_CALL events → RUN_FINISHED(success)
/// Turn 2: Client sends continuation with client tool results → FICC invokes server tool
///         for real and uses cached client result → stream converter emits only server
///         TOOL_CALL_RESULT + final text
/// </summary>
public sealed class MixedToolInvocationIntegrationTest : IntegrationTestBase
{
    public MixedToolInvocationIntegrationTest(WebApplicationFactory<Program> factory)
        : base(factory)
    {
    }

    [Fact]
    public async Task FICC_ApprovalFlow_DirectTest()
    {
        // Test FICC approval processing directly without AG-UI
        var toolInvoked = false;
        var serverTool = AIFunctionFactory.Create(() => { toolInvoked = true; return "result"; }, "my_tool", "desc");

        var fakeLlm = new FakeChatClientWithCapture();
        // After approval processing, FICC should invoke the tool then call LLM
        fakeLlm.Enqueue(_ => EmitTextResponse("done"));

        var ficc = new ChatClientBuilder(fakeLlm)
            .UseFunctionInvocation()
            .Build();

        var fcc = new FunctionCallContent("call_1", "my_tool", new Dictionary<string, object?>());
        var request = new ToolApprovalRequestContent("req_1", fcc);
        var response = request.CreateResponse(approved: true);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "test"),
            new(ChatRole.Assistant, [request]),
            new(ChatRole.User, [response]),
        };

        var options = new ChatOptions { Tools = [serverTool] };
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in ficc.GetStreamingResponseAsync(messages, options))
        {
            updates.Add(u);
        }

        Assert.True(toolInvoked, "Tool should be invoked via approval flow");
    }

    [Fact]
    public async Task MixedInvocation_TwoTurnFlow_EmitsToolCallsThenServerResults()
    {
        // Server tool: registered server-side, will be executed on the server during turn 2
        var serverToolInvoked = false;
        string GetWeather()
        {
            serverToolInvoked = true;
            return "Weather in Amsterdam: 22C, sunny";
        }

        var serverTool = AIFunctionFactory.Create(GetWeather, "get_weather", "Gets the weather");

        var fakeLlm = new FakeChatClientWithCapture();

        // Turn 1 (server): LLM emits both tool calls → FICC wraps in approval requests
        fakeLlm.Enqueue(_ => EmitMixedToolCalls());

        // Turn 2 (server): After approval processing, FICC calls LLM for final text.
        fakeLlm.Enqueue(_ => EmitTextResponse(
            "Based on the weather in Amsterdam (22C, sunny) and your location (Amsterdam), I recommend visiting Vondelpark!"));

        var factory = Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatClient>();
                services.AddSingleton<DelegatingStreamingChatClient>();
                services.AddSingleton<AITool>(serverTool);
                services.AddChatClient(sp => (IChatClient)fakeLlm)
                    .UseFunctionInvocation();
            });
        });

        var httpClient = factory.CreateClient();
        var transport = new AGUIHttpTransport(httpClient, "/agui");
        var aguiClient = new AGUIChatClient(transport);

        // Client declares get_user_location as a client tool with a REAL implementation
        // (AGUIChatClient's internal FICC will auto-invoke this)
        var clientToolInvoked = false;
        var clientTool = AIFunctionFactory.Create(
            () =>
            {
                clientToolInvoked = true;
                return "Amsterdam, Netherlands (52.37N, 4.90E)";
            },
            "get_user_location",
            "Gets the user's GPS location");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "What's the weather near me?")
        };
        var options = new ChatOptions
        {
            Tools = [clientTool]
        };

        // Single call: AGUIChatClient's FICC handles the full round-trip internally
        // Turn 1: server emits tool calls → client invokes get_user_location → sends result back
        // Turn 2: server processes continuation, invokes get_weather, calls LLM for final text
        var updates = await CollectUpdates(aguiClient, messages, options);

        // Client tool was auto-invoked by AGUIChatClient's internal FICC
        Assert.True(clientToolInvoked, "Client tool should be auto-invoked by AGUIChatClient");

        // Server tool was invoked on the continuation turn
        Assert.True(serverToolInvoked, "Server tool should be invoked during continuation");

        // The server tool call (get_weather) was marked InformationalOnly by AGUIChatClientHandler
        // so it passes through to the caller as a FunctionCallContent
        var serverToolCalls = updates
            .SelectMany(u => u.Contents)
            .OfType<FunctionCallContent>()
            .Where(fcc => fcc.Name == "get_weather")
            .ToList();
        Assert.Single(serverToolCalls);

        // Final text response should be present
        var text = ExtractText(updates);
        Assert.Contains("Vondelpark", text);
    }

    [Fact]
    public async Task MixedInvocation_ServerOnlyToolCalls_NoApprovalFlow()
    {
        // When the LLM only calls server tools (not client tools), the normal
        // execution flow proceeds without the approval mechanism.
        var serverToolInvoked = false;
        string GetWeather(string city)
        {
            serverToolInvoked = true;
            return $"Weather in {city}: 18C, cloudy";
        }

        var serverTool = AIFunctionFactory.Create(GetWeather, "get_weather", "Gets the weather for a city");

        var fakeLlm = new FakeChatClientWithCapture();

        // Turn 1: LLM calls only the server tool
        fakeLlm.Enqueue(_ => EmitSingleToolCall("call_w1", "get_weather",
            new Dictionary<string, object?> { ["city"] = "London" }));
        // Turn 2: LLM produces final text after seeing server tool result
        fakeLlm.Enqueue(_ => EmitTextResponse("The weather in London is 18C and cloudy."));

        var factory = Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatClient>();
                services.AddSingleton<DelegatingStreamingChatClient>();
                services.AddSingleton<AITool>(serverTool);
                services.AddChatClient(sp => (IChatClient)fakeLlm)
                    .UseFunctionInvocation();
            });
        });

        var httpClient = factory.CreateClient();
        var transport = new AGUIHttpTransport(httpClient, "/agui");
        var aguiClient = new AGUIChatClient(transport);

        // Client declares a client tool, but LLM only calls the server tool
        var clientTool = AIFunctionFactory.Create(() => "stub", "get_user_location", "Gets location");
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "What's the weather in London?")
        };
        var options = new ChatOptions { Tools = [clientTool] };

        var updates = await CollectUpdates(aguiClient, messages, options);

        // Server tool should have been invoked (FICC executed it in the tool loop)
        Assert.True(serverToolInvoked);

        // Final text should be present
        var text = ExtractText(updates);
        Assert.Contains("18C", text);

        // Should be a single run: RunStarted + text + RunFinished(success)
        var runFinished = updates.FirstOrDefault(u => u.RawRepresentation is RunFinishedEvent);
        Assert.NotNull(runFinished);
        Assert.Equal(ChatFinishReason.Stop, runFinished!.FinishReason);
    }

    [Fact]
    public async Task MixedInvocation_ClientOnlyToolCalls_TwoTurnFlow()
    {
        // When the LLM only calls client tools, the two-turn flow still works:
        // AGUIChatClient's FICC auto-invokes the client tool and sends the result
        // back to the server, which processes the continuation and calls the LLM.
        var clientToolInvoked = false;
        var fakeLlm = new FakeChatClientWithCapture();

        // Turn 1 (server): LLM calls only the client tool
        fakeLlm.Enqueue(_ => EmitSingleToolCall("call_loc1", "get_user_location",
            new Dictionary<string, object?>()));
        // Turn 2 (server): After continuation processing, LLM produces text
        fakeLlm.Enqueue(_ => EmitTextResponse("You are in Amsterdam!"));

        var factory = Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatClient>();
                services.AddSingleton<DelegatingStreamingChatClient>();
                services.AddChatClient(sp => (IChatClient)fakeLlm)
                    .UseFunctionInvocation();
            });
        });

        var httpClient = factory.CreateClient();
        var transport = new AGUIHttpTransport(httpClient, "/agui");
        var aguiClient = new AGUIChatClient(transport);

        var clientTool = AIFunctionFactory.Create(
            () =>
            {
                clientToolInvoked = true;
                return "Amsterdam, Netherlands";
            },
            "get_user_location",
            "Gets location");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Where am I?")
        };
        var options = new ChatOptions { Tools = [clientTool] };

        // Single call: AGUIChatClient handles the full flow
        var updates = await CollectUpdates(aguiClient, messages, options);

        // Client tool was auto-invoked
        Assert.True(clientToolInvoked);

        // Final text
        var text = ExtractText(updates);
        Assert.Contains("Amsterdam", text);
    }

#pragma warning disable CS1998
    private static async IAsyncEnumerable<ChatResponseUpdate> EmitMixedToolCalls(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new FunctionCallContent("call_weather_1", "get_weather",
                    new Dictionary<string, object?> { ["city"] = "Amsterdam" }),
                new FunctionCallContent("call_location_1", "get_user_location",
                    new Dictionary<string, object?>())
            ],
            FinishReason = ChatFinishReason.ToolCalls
        };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmitSingleToolCall(
        string callId, string name, IDictionary<string, object?> arguments,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent(callId, name, arguments)],
            FinishReason = ChatFinishReason.ToolCalls
        };
    }

#pragma warning restore CS1998

    /// <summary>
    /// A fake chat client that uses a queue of handlers.
    /// Each handler is called once per LLM turn.
    /// </summary>
    private sealed class FakeChatClientWithCapture : IChatClient
    {
        private readonly Queue<Func<IEnumerable<ChatMessage>, IAsyncEnumerable<ChatResponseUpdate>>> _handlers = new();

        internal void Enqueue(Func<IEnumerable<ChatMessage>, IAsyncEnumerable<ChatResponseUpdate>> handler)
        {
            _handlers.Enqueue(handler);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_handlers.Count == 0)
            {
                throw new InvalidOperationException("No handler enqueued on FakeChatClientWithCapture.");
            }

            var handler = _handlers.Dequeue();
            await foreach (var update in handler(messages).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceType == typeof(IChatClient))
            {
                return this;
            }

            return null;
        }

        public void Dispose() { }
    }
}
