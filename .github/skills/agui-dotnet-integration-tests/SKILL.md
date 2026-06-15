---
name: agui-dotnet-integration-tests
description: 'Write integration tests for the AG-UI .NET SDK. Use when adding new AG-UI event types, testing SSE streaming, verifying AGUIChatClient behavior, testing multi-turn conversations, or writing end-to-end tests for AG-UI protocol features. Covers: WebApplicationFactory setup, DelegatingStreamingChatClient pattern, IChatClient-based test infrastructure, SSE event assertion with Assert.Collection, AGUIChatClient multi-turn tests, recording/replay snapshot tests for samples.'
---

# AG-UI .NET Integration Tests

## When to Use
- Adding a new AG-UI event type and need integration test coverage
- Testing SSE streaming of events through the hosting pipeline
- Verifying AGUIChatClient correctly maps events to ChatResponseUpdate
- Testing multi-turn conversation flows with message history
- Writing end-to-end tests for new AG-UI protocol features
- Adding or modifying sample applications

## Project Layout

```
tests/AGUI.Hosting.AspNetCore.IntegrationTests/
├── IntegrationTestBase.cs              # Two-tier base: generic <TProgram> and non-generic (uses Program)
├── DelegatingStreamingChatClient.cs    # Func-based IChatClient for inline test setup
├── CapturingChatClient.cs              # IChatClient decorator that records server-side calls
├── CapturingAGUITransport.cs           # IAGUITransport decorator that records client-side turns
├── ServerCallCapture.cs                # Captures RunAgentInput + ChatMessage + ChatResponseUpdate per server call
├── TurnCapture.cs                      # Captures RunAgentInput + BaseEvent per client-side turn
├── AGUIServerSentEventsResult.cs       # IResult that writes BaseEvent stream as SSE
├── AGUIEndpointExtensions.cs           # Test-local MapAGUI endpoint built on ToChatRequestContext + AsAGUIEventStreamAsync (mirrors production pattern)
├── AGUICapabilitiesEndpointRouteBuilderExtensions.cs # Test-only convenience for publishing AgentCapabilities (the SDK does not ship a public capabilities endpoint helper since the AG-UI spec does not describe how capabilities are exposed)
├── VerifyConfig.cs                     # ModuleInitializer for Verify.Xunit settings
├── Program.cs                          # Minimal hosting app (DelegatingStreamingChatClient + MapAGUI)
├── RunLifecycleIntegrationTest.cs      # Run lifecycle event tests
├── TextStreamingIntegrationTest.cs     # Text streaming + multi-turn tests
├── ToolCallIntegrationTest.cs          # Tool call event tests
├── ClientToolIntegrationTest.cs        # Client (frontend) tool tests
├── MixedToolInvocationIntegrationTest.cs # Mixed server+client tool batches (approval-flow pipeline)
├── StateManagementIntegrationTest.cs   # State snapshot/delta tests
├── ReasoningIntegrationTest.cs         # Thinking/reasoning event tests
├── ActivityIntegrationTest.cs          # Activity snapshot/delta tests
├── CustomAndRawEventIntegrationTest.cs # Custom event tests
├── CapabilitiesEndpointIntegrationTest.cs  # Capabilities endpoint tests (exercises the test-local helper)
├── RawRepresentationFactoryIntegrationTest.cs # Custom RawRepresentation factory tests
├── Samples/GettingStarted/             # Recording/replay tests for each sample
│   ├── Step01_GettingStartedTest.cs
│   ├── Step02_BackendToolsTest.cs
│   ├── ... through Step11_SerializationTest.cs
│   ├── *.recording.json                # Pre-recorded ChatResponseUpdate sequences
│   └── *.verified.txt                  # Verify.Xunit snapshots
└── AGUI.Hosting.AspNetCore.IntegrationTests.csproj
```

## Architecture

### IChatClient-based Design
The test infrastructure is built on `Microsoft.Extensions.AI.IChatClient`. The server side always works with `ChatResponseUpdate` streams. The AG-UI hosting layer converts these to `BaseEvent` SSE streams on the wire. Tests verify behavior at the `ChatResponseUpdate` level.

### DelegatingStreamingChatClient Pattern
Tests define agent behavior inline via a `Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>` delegate:

```csharp
var client = CreateClient((messages, options, ct) =>
    EmitTextResponse("Hello!"));
```

`CreateClient` creates a fresh `AGUIChatClient` backed by a `WebApplicationFactory<Program>` HTTP pipeline:

```csharp
protected AGUIChatClient CreateClient(
    Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> handler)
{
    var httpClient = Factory.WithWebHostBuilder(builder =>
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(sp =>
            {
                var client = new DelegatingStreamingChatClient();
                client.SetHandler(handler);
                return client;
            });
        });
    }).CreateClient();

    return new AGUIChatClient(httpClient, "/agui");
}
```

### Two-Tier Base Class
- `IntegrationTestBase<TProgram>` — generic base that works with any `Program` type. Provides `CollectUpdates`, `EmitTextResponse`, `EmitToolCallResponse`, `EmitToolCallWithResultResponse`, `CreateToolDeclaration`, and `ExtractText` helpers.
- `IntegrationTestBase` — non-generic subclass that extends `IntegrationTestBase<Program>`, adds the content root env-var fix for `.slnx`, and provides the `CreateClient` method that injects `DelegatingStreamingChatClient`.

### Content Root Fix for .slnx
The non-generic constructor sets the content root env var so `WebApplicationFactory` finds the app:

```csharp
Environment.SetEnvironmentVariable(
    "ASPNETCORE_TEST_CONTENTROOT_AGUI_HOSTING_ASPNETCORE_INTEGRATIONTESTS",
    AppContext.BaseDirectory);
```

### Emit Helpers
All emit helpers yield `ChatResponseUpdate` objects (not `BaseEvent`). The hosting pipeline converts them to events.

```csharp
// Text
protected static async IAsyncEnumerable<ChatResponseUpdate> EmitTextResponse(string text)
{
    yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Text = text };
    await Task.CompletedTask;
}

// Tool call (FunctionCallContent)
protected static async IAsyncEnumerable<ChatResponseUpdate> EmitToolCallResponse(
    string callId, string name, Dictionary<string, object?> args)
{
    yield return new ChatResponseUpdate
    {
        Role = ChatRole.Assistant,
        Contents = [new FunctionCallContent(callId, name, args)]
    };
    await Task.CompletedTask;
}

// Tool call + result (two updates)
protected static async IAsyncEnumerable<ChatResponseUpdate> EmitToolCallWithResultResponse(
    string callId, string name, Dictionary<string, object?> args, string result)
{
    yield return new ChatResponseUpdate
    {
        Role = ChatRole.Assistant,
        Contents = [new FunctionCallContent(callId, name, args)]
    };
    yield return new ChatResponseUpdate
    {
        Role = ChatRole.Assistant,
        Contents = [new FunctionResultContent(callId, result)]
    };
    await Task.CompletedTask;
}
```

### CollectUpdates Helper
Drives a full turn through the `AGUIChatClient` and collects all streaming updates:

```csharp
protected static async Task<List<ChatResponseUpdate>> CollectUpdates(
    AGUIChatClient client,
    IEnumerable<ChatMessage> messages,
    ChatOptions? options = null)
{
    var updates = new List<ChatResponseUpdate>();
    await foreach (var update in client.GetStreamingResponseAsync(messages, options))
    {
        updates.Add(update);
    }
    return updates;
}
```

## Procedure: Writing a New Integration Test

### Step 1: Decide which test file
- **Run lifecycle events** → `RunLifecycleIntegrationTest.cs`
- **Text streaming events** → `TextStreamingIntegrationTest.cs`
- **Tool calls** → `ToolCallIntegrationTest.cs`
- **Client (frontend) tools** → `ClientToolIntegrationTest.cs`
- **Mixed server+client tool batches** → `MixedToolInvocationIntegrationTest.cs`
- **State management** → `StateManagementIntegrationTest.cs`
- **Reasoning / thinking** → `ReasoningIntegrationTest.cs`
- **Activity** → `ActivityIntegrationTest.cs`
- **Custom / raw events** → `CustomAndRawEventIntegrationTest.cs`
- **Capabilities endpoint** → `CapabilitiesEndpointIntegrationTest.cs`
- **New event category** → Create `{Category}IntegrationTest.cs` inheriting `IntegrationTestBase`

### Step 2: Create the test class

```csharp
public sealed class MyFeatureIntegrationTest : IntegrationTestBase
{
    public MyFeatureIntegrationTest(WebApplicationFactory<Program> factory)
        : base(factory)
    {
    }
}
```

### Step 3: Write the test
Tests go through the `AGUIChatClient` (`IChatClient`) abstraction. The client receives `ChatResponseUpdate` objects whose `RawRepresentation` property carries the underlying `BaseEvent`.

```csharp
[Fact]
public async Task PostRun_TextContent_MapsToTextMessageEvents()
{
    var client = CreateClient((messages, options, ct) =>
        EmitTextResponse("Hello world!"));

    var updates = await CollectUpdates(client, [new ChatMessage(ChatRole.User, "Hi")]);

    Assert.Collection(updates,
        u =>
        {
            Assert.Equal(ChatRole.Assistant, u.Role);
            Assert.IsType<RunStartedEvent>(u.RawRepresentation);
        },
        u =>
        {
            Assert.Equal(ChatRole.Assistant, u.Role);
            Assert.Equal("Hello world!", u.Text);
            Assert.IsType<TextMessageContentEvent>(u.RawRepresentation);
        },
        u =>
        {
            Assert.Equal(ChatFinishReason.Stop, u.FinishReason);
            Assert.IsType<RunFinishedEvent>(u.RawRepresentation);
        });
}
```

### Step 4: Multi-turn tests
Use a queue-based handler pattern:

```csharp
[Fact]
public async Task PostRun_MultiTurn_MessagesConvertedFromChatToAGUI()
{
    var capturedMessages = new List<IList<ChatMessage>>();

    var turns = new Queue<Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>>();
    turns.Enqueue((messages, options, ct) =>
    {
        capturedMessages.Add(messages.ToList());
        return EmitTextResponse("Response 1");
    });
    turns.Enqueue((messages, options, ct) =>
    {
        capturedMessages.Add(messages.ToList());
        return EmitTextResponse("Response 2");
    });

    var client = CreateClient((messages, options, ct) => turns.Dequeue()(messages, options, ct));

    // Turn 1
    var updates1 = await CollectUpdates(client, [new ChatMessage(ChatRole.User, "Hello")]);
    Assert.Single(capturedMessages[0]);

    // Turn 2 - includes full history
    var turn2Messages = new List<ChatMessage>
    {
        new(ChatRole.User, "Hello"),
        new(ChatRole.Assistant, "Response 1"),
        new(ChatRole.User, "Follow up")
    };
    var updates2 = await CollectUpdates(client, turn2Messages);
    Assert.Equal(3, capturedMessages[1].Count);
}
```

### Step 5: Tool call tests
Use `DummyClientTool` (a static `AGUITool` on the base class created via `CreateToolDeclaration`) and pass it as `ChatOptions.Tools`:

```csharp
[Fact]
public async Task PostRun_FunctionCallContent_MapsToToolCallEvents()
{
    var client = CreateClient((messages, options, ct) =>
        EmitToolCallResponse("call-1", "get_weather",
            new Dictionary<string, object?> { ["location"] = "Seattle" }));

    var updates = await CollectUpdates(
        client,
        [new ChatMessage(ChatRole.User, "Hi")],
        new ChatOptions { Tools = [DummyClientTool] });

    Assert.Collection(updates,
        u => Assert.IsType<RunStartedEvent>(u.RawRepresentation),
        u =>
        {
            var fcc = Assert.Single(u.Contents.OfType<FunctionCallContent>());
            Assert.Equal("get_weather", fcc.Name);
            Assert.Equal("call-1", fcc.CallId);
        },
        u => Assert.Equal(ChatFinishReason.ToolCalls, u.FinishReason));
}
```

### Step 6: Add an Emit helper (if needed)
If a new event category needs a reusable emitter, add it to `IntegrationTestBase.cs`. Helpers yield `ChatResponseUpdate` — **not** `BaseEvent`. The hosting pipeline handles the conversion.

## Sample Recording/Replay Tests

Tests under `Samples/GettingStarted/` use a recording/replay infrastructure to test each sample deterministically:

### Key Types
- **`FakeChatClient`** — an `IChatClient` with a `Queue<Func<...>>` of handlers. Each call to `GetStreamingResponseAsync` dequeues the next handler. Each sample defines its own `FakeChatClient` (same pattern, different namespace).
- **`CapturingChatClient`** — decorates the real `IChatClient`, records every `GetStreamingResponseAsync` call as a `ServerCallCapture` (the `RunAgentInput`, `ChatMessage` list, and resulting `ChatResponseUpdate` list).
- **`CapturingAGUITransport`** — decorates the `IAGUITransport`, records every `SendAsync` call as a `TurnCapture` (the `RunAgentInput` and resulting `BaseEvent` list).

### Pattern
Each sample test inherits `IntegrationTestBase<StepNN_XYZ.Program>` and provides `CreateCapturingClient`:

```csharp
public sealed class Step01_GettingStartedTest : IntegrationTestBase<Step01_GettingStarted.Program>
{
    [Fact]
    public async Task PostRun_MultiTurn_SynthesizesAssistantMessages()
    {
        var (aguiClient, transport, server) = CreateCapturingClient(turnCount: 2);

        // Turn 1
        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        var turn1Updates = await CollectUpdates(aguiClient, messages);

        // Synthesize assistant message from turn 1
        messages.AddMessages(turn1Updates.ToChatResponse());
        messages.Add(new ChatMessage(ChatRole.User, "How are you?"));

        // Turn 2
        var turn2Updates = await CollectUpdates(aguiClient, messages);

        await VerifyAllCaptures(transport, server, clientMessages, clientUpdates);
    }
}
```

`CreateCapturingClient` loads a `.recording.json` file, replays the updates through `FakeChatClient`, and wraps everything with the capturing decorators. `VerifyAllCaptures` produces a snapshot verified by `Verify.Xunit`.

### Recording Files
- `*.recording.json` — serialized `List<List<ChatResponseUpdate>>` (one list per turn).
- `*.verified.txt` — Verify.Xunit snapshot of the full capture output.

When adding a new sample, create both files. When a snapshot changes, review the `.received.txt` diff before accepting.

## Key Rules

1. **Always use `Assert.Collection`** for event sequences — it validates count AND order AND properties in one call
2. **Assert event properties** inside each `Assert.Collection` inspector — don't just check type
3. **ChatResponseUpdate, not BaseEvent** — test helpers emit `ChatResponseUpdate`. Check `u.RawRepresentation` to inspect the underlying AG-UI event type
4. **`CreateClient` returns `AGUIChatClient`** — not `HttpClient`. Tests drive behavior through the `IChatClient` abstraction
5. **Use `CollectUpdates`** — not manual `await foreach` loops — to collect all updates for a turn
6. **No separate agent classes** — use `DelegatingStreamingChatClient` + inline handler for every test
7. **Queue-based pattern** for multi-turn: `var turns = new Queue<Func<...>>()` with `turns.Dequeue()`
8. **The hosting pipeline wraps in RunStarted/RunFinished** — emit helpers don't need to produce lifecycle events
9. **One test file per event category** — separate concerns into dedicated test classes
10. **Test file naming**: `{Category}IntegrationTest.cs`
11. **Sample tests inherit `IntegrationTestBase<SampleProgram>`** — not the non-generic `IntegrationTestBase`
12. **Snapshot tests use `VerifyAllCaptures`** — which combines client-side transport captures and server-side chat client captures into a single Verify snapshot
