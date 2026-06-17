# Cross-Language E2E Testing: .NET ↔ TypeScript Interop

## Goal

Verify that the C# server and C# client are **fully interoperable** with the TypeScript ecosystem in end-to-end scenarios — not just wire-format conformance, but the complete story: a user sends a message, the server talks to an LLM, transforms the response into AG-UI events, and the client produces the correct end result.

Concretely:
1. **C# server + TS client**: The existing dojo e2e tests (Playwright) that currently run against TS server backends should also run against the C# ASP.NET Core server — proving the C# server produces events that the TS client (CopilotKit React) can render correctly.
2. **TS server + C# client**: The same scenarios running against TS servers should be consumable by the C# `AGUIChatClient` — proving the C# client handles real-world TS server output correctly and produces the same end results.

The C# server's responses may differ slightly from TS server responses (different chunking, field ordering, optional fields), but must produce the **same end results** from the client's perspective.

## How the Existing E2E Tests Work

### The Stack

```
┌──────────────────────────────────────────────────────────────────┐
│  Playwright (browser)                                             │
│  └── CopilotKit React UI (TS client consuming AG-UI events)      │
│      └── HttpAgent → POST /agui → SSE events                     │
├──────────────────────────────────────────────────────────────────┤
│  AG-UI Server (e.g., server-starter, langgraph-typescript, etc.)  │
│  └── Calls upstream LLM API (OpenAI chat/completions)             │
├──────────────────────────────────────────────────────────────────┤
│  @copilotkit/aimock (LLMock on port 5555)                         │
│  └── Mocks OpenAI API with fixture-based responses                │
│      (e.g., "I am duaa" → "Hello duaa!")                          │
└──────────────────────────────────────────────────────────────────┘
```

### Key Components

- **LLMock** (`@copilotkit/aimock`): Runs on port 5555, intercepts calls to `OPENAI_BASE_URL`. Returns canned completions/tool-calls based on message content matching.
- **Fixtures** (`apps/dojo/e2e/fixtures/openai/*.json`): Define LLM responses per user message (e.g., "I am duaa" → text response, "background color to blue" → tool call).
- **Dojo app** (`apps/dojo/`): React app with CopilotKit that renders chat UI against multiple server backends.
- **Playwright specs** (`apps/dojo/e2e/tests/`): Assert UI behavior — messages appear, tools execute, state updates render.

### Existing .NET Coverage

There's already a `microsoftAgentFrameworkDotnetTests/` folder with a basic test:
```typescript
test("[MS Agent Framework .NET] Agentic Chat sends and receives a message", async ({ page }) => {
  await page.goto("/microsoft-agent-framework-dotnet/feature/agentic_chat");
  const chat = new AgenticChatPage(page);
  await chat.openChat();
  await chat.sendMessage("Hi, I am duaa");
  await chat.assertAgentReplyVisible(/Hello/i);
});
```

This proves the pattern already works. We need to **expand it** to cover all the scenarios that other servers test.

## Recording Fixtures Against a Real LLM

The cross-language Vitest fixtures (`tests/CrossLanguage.Vitest/fixtures/*.json`) are
deterministic, but they can be **recorded from a real LLM** so we know the C# server is
compliant with actual model output — the same principle as the .NET integration tests.

### Topology

AIMock is the LLM stand-in; it sits between the C# server and the real model, not between
the client and the server:

```
Record:   TS Client ──AG-UI──► C# Server ──OpenAI──► AIMock (proxy + save) ──► real LLM
Replay:   TS Client ──AG-UI──► C# Server ──OpenAI──► AIMock (match fixture, no network)
```

The fixture captures the **LLM's OpenAI chat-completion response**, not the C# server's
AG-UI output. The client↔server AG-UI exchange is re-derived live and deterministically on
every replay run, so replay still exercises the real C# mapping each time — only the LLM is
frozen.

### How it works

`helpers/llmock.ts` exposes a `record` option that calls AIMock's `enableRecording`. When a
request has no matching fixture, AIMock proxies it to the configured upstream, saves the
collapsed response under `fixtures/recorded/` (gitignored), and relays it back. Because the
C# server speaks plain OpenAI to AIMock (`POST /v1/chat/completions`), AIMock joins the
upstream base with the path — so pointing the upstream at Azure's OpenAI **v1** surface
(`https://<resource>.cognitiveservices.azure.com/openai`) yields
`.../openai/v1/chat/completions`. Auth is forwarded verbatim: the C# server presents the
`OPENAI_API_KEY` as a bearer token, so an Entra ID (AAD) token works against Azure.

`helpers/record-config.ts` resolves recording from the environment (see below) and mints an
AAD token via `az account get-access-token` when `OPENAI_API_KEY` isn't supplied.

### Environment variables

| Variable | Purpose |
| --- | --- |
| `AIMOCK_RECORD=true` | Enable recording (proxy unmatched calls). |
| `AZURE_OPENAI_ENDPOINT` | Azure resource endpoint; upstream becomes `<endpoint>/openai`. |
| `AIMOCK_RECORD_UPSTREAM` | Explicit upstream base (overrides the Azure derivation). |
| `OPENAI_CHAT_MODEL_ID` | Model / Azure deployment (default `gpt-5-mini` in record mode). |
| `OPENAI_API_KEY` | Explicit key/token; otherwise an AAD token is minted via `az`. |

### Workflow (PowerShell)

```powershell
cd sdks/dotnet/tests/CrossLanguage.Vitest
$env:AIMOCK_RECORD = "true"
$env:AZURE_OPENAI_ENDPOINT = "https://<resource>.cognitiveservices.azure.com"
$env:OPENAI_CHAT_MODEL_ID = "gpt-5-mini"
# az login first; an AAD token is minted automatically.
npx vitest run tests/<scenario>.test.ts
```

Recording **fills gaps**: committed fixtures are still loaded first, so delete a committed
fixture (or omit it) to force its scenario to re-record. Captured files land in
`fixtures/recorded/` for you to curate into the named `fixtures/*.json`.

### Multi-turn scenarios: match the tool-result turn, not the user message

After a tool call the client replays the **same conversation** with the tool result
appended — the last *user* message is unchanged. So a turn-2 fixture matched on
`userMessage` would re-match the turn-1 fixture and loop. AIMock's documented fix
(`write-fixtures` skill, gotcha #5) is to match the **tool-result turn**:

- Programmatically: `predicate: (req) => req.messages.at(-1)?.role === "tool"`.
- In JSON fixtures (our case, since `predicate` is a function): the **`toolCallId`** field —
  "exact match on `tool_call_id` of the last `role: "tool"` message".

List the specific tool-result fixture **first** so it wins on turn 2; turn 1 (no tool
message) falls through to the `userMessage` fixture. This is turn-count-independent — no
`sequenceIndex` needed. The committed `mixed-tool-invocation.json` uses exactly this shape:

```jsonc
{
  "fixtures": [
    // Turn 2: the continuation's last tool result is get_weather's (resolved server-side
    // via FICC), so match its call id and return the final text.
    { "match": { "toolCallId": "call_XHrYFpdLMi6841ix1cwXZyy6" },
      "response": { "content": "Your current city is Tokyo, Japan. ... Berlin ..." } },
    // Turn 1: first request, no tool result yet — surface both tool calls.
    { "match": { "userMessage": "What is my current city and the forecast for Berlin?" },
      "response": { "toolCalls": [ /* get_user_location, get_weather */ ] } }
  ]
}
```

#### Capturing both turns from the LLM (two passes)

The *recorder* still keys every capture on the last user message (`buildFixtureMatch`) and
caches it in memory, so a single record pass only captures turn 1 (it then shadows turn 2).
To capture turn 2's real response:

1. **Pass 1** — record turn 1, then curate the capture into the committed fixture as
   `{ "userMessage": ..., "sequenceIndex": 0 }` (temporary — gates it to the first occurrence).
2. **Pass 2** — re-run recording. Turn 1 replays; turn 2 is the 2nd occurrence, skips the
   `sequenceIndex: 0` entry, proxies to the LLM, and is captured.
3. **Finalize** — rewrite the committed fixture into the turn-count-independent form above
   (turn-2 `toolCallId` first, turn-1 `userMessage` second, drop `sequenceIndex`).

Single-turn scenarios need only one pass. After curating, clear `OPENAI_API_KEY`/
`AIMOCK_RECORD` and re-run to confirm the scenario replays offline and green.

## Approach

### Scenario A: Expand Dojo E2E Tests for C# Server

The C# server (`AGUIDojoServer` or a new variant) backs the same dojo routes that the `server-starter-all-features` tests exercise. We reuse the **same Playwright specs** with the C# backend.

**What we need:**
1. A C# server that handles the same features as `server-starter-all-features`:
   - Agentic chat (text streaming)
   - Backend tool calls
   - Frontend/client tool calls (human-in-the-loop)
   - Shared state (state snapshot + delta)
   - Custom events
   - Reasoning/thinking events

2. The C# server uses `Microsoft.Extensions.AI` `IChatClient` to call the LLM — pointed at LLMock (`http://localhost:5555/v1`) instead of real OpenAI.

3. The C# server is already registered as a dojo route (`/microsoft-agent-framework-dotnet/feature/{feature}`) — Playwright navigates to it directly.

4. Expand the Playwright specs for the `.NET` backend to cover all the same scenarios as `serverStarterAllFeaturesTests`.

**Implementation path:**
- The existing `samples/AGUIClientServer/AGUIDojoServer/` already has tool calls, state management, etc.
- It's already wired into the dojo app and started via `run-dojo-everything.js` on port 8016
- Ensure `OPENAI_BASE_URL=http://localhost:5555/v1` is set so LLMock handles the AI responses
- Expand the `microsoftAgentFrameworkDotnetTests` specs to match `serverStarterAllFeaturesTests` coverage

### Scenario B: C# Client Against TS Servers

Same approach as above but reversed. We run the TS `server-starter` as a real process backed by LLMock (exactly as the dojo e2e tests do), and connect with the C# `AGUIChatClient`.

The C# test project:
1. Starts LLMock (port 5555) — same as dojo's `aimock-setup.ts`
2. Starts the TS server-starter (port 5100) — pointed at LLMock via `OPENAI_BASE_URL`
3. Connects with `AGUIChatClient` to `http://localhost:5100/agui`
4. Sends the same messages the Playwright tests send
5. Asserts the `AGUIChatClient` receives correct `ChatResponseUpdate` output (same messages, same tool calls, same content)

```
┌──────────────────────────────────────────────────────────────────┐
│  C# Integration Test (xUnit)                                      │
│  └── AGUIChatClient → POST http://localhost:5100/agui → SSE      │
├──────────────────────────────────────────────────────────────────┤
│  TS AG-UI Server (server-starter, real process on port 5100)      │
│  └── Calls http://localhost:5555/v1/chat/completions (LLMock)    │
├──────────────────────────────────────────────────────────────────┤
│  @copilotkit/aimock (LLMock, real process on port 5555)           │
│  └── Returns canned responses from fixtures/openai/*.json         │
└──────────────────────────────────────────────────────────────────┘
```

## Detailed Design

### Directory Structure

```
sdks/dotnet/tests/
├── AGUI.CrossLanguage.IntegrationTests/    # C# client → TS server tests
│   ├── AGUI.CrossLanguage.IntegrationTests.csproj
│   ├── TsServerFixture.cs                  # Manages LLMock + TS server processes
│   ├── AgenticChatTest.cs                  # Chat scenarios
│   ├── ToolCallTest.cs                     # Backend/frontend tool scenarios
│   ├── StateManagementTest.cs              # Shared state scenarios
│   └── ReasoningTest.cs                    # Reasoning/thinking scenarios
└── cross-language/                         # Vitest: TS client → C# server tests
    ├── package.json
    ├── vitest.config.ts
    ├── src/
    │   ├── dotnet-server.ts                # Start/stop C# server process
    │   └── llmock-setup.ts                 # Start/stop LLMock with fixtures
    └── tests/
        ├── agentic-chat.test.ts            # TS HttpAgent → C# server
        ├── tool-calls.test.ts
        ├── state-management.test.ts
        └── reasoning.test.ts
```

### Part 1: TS Client → C# Server (Vitest)

These tests use the TS `HttpAgent` (from `@ag-ui/client`) against the real C# server, with LLMock providing the upstream LLM responses.

```typescript
// tests/agentic-chat.test.ts
import { describe, it, expect, beforeAll, afterAll } from "vitest";
import { HttpAgent } from "@ag-ui/client";
import { EventType, BaseEvent, RunAgentInput } from "@ag-ui/core";
import { firstValueFrom, toArray } from "rxjs";
import { startDotnetServer, stopDotnetServer } from "../src/dotnet-server";
import { startLLMock, stopLLMock } from "../src/llmock-setup";

describe("TS HttpAgent → C# server (with LLMock)", () => {
  let serverPort: number;

  beforeAll(async () => {
    await startLLMock();      // Port 5555, loads agentic-chat.json fixtures
    serverPort = await startDotnetServer(); // Points OPENAI_BASE_URL at LLMock
  }, 60_000);

  afterAll(async () => {
    await stopDotnetServer();
    await stopLLMock();
  });

  it("receives text response for simple chat", async () => {
    const agent = new HttpAgent({ url: `http://localhost:${serverPort}/agui` });
    const input: RunAgentInput = {
      threadId: "t1",
      runId: "r1",
      messages: [{ id: "m1", role: "user", content: "Hi, I am duaa" }],
      tools: [],
      context: [],
    };

    const events = await firstValueFrom(agent.run(input).pipe(toArray()));
    
    // Verify the lifecycle events are present
    expect(events[0].type).toBe(EventType.RUN_STARTED);
    expect(events[events.length - 1].type).toBe(EventType.RUN_FINISHED);
    
    // Verify text content includes expected response
    const textEvents = events.filter(e => e.type === EventType.TEXT_MESSAGE_CONTENT);
    const fullText = textEvents.map(e => (e as any).delta).join("");
    expect(fullText).toMatch(/Hello.*duaa/i);
  });

  it("executes backend tool call", async () => {
    const agent = new HttpAgent({ url: `http://localhost:${serverPort}/agui` });
    const input: RunAgentInput = {
      threadId: "t1",
      runId: "r1",
      messages: [{ id: "m1", role: "user", content: "stock price of AAPL" }],
      tools: [],
      context: [],
    };

    const events = await firstValueFrom(agent.run(input).pipe(toArray()));
    
    const textEvents = events.filter(e => e.type === EventType.TEXT_MESSAGE_CONTENT);
    const fullText = textEvents.map(e => (e as any).delta).join("");
    expect(fullText).toContain("150.25");
  });
});
```

### Part 2: C# Client → TS Server (xUnit)

These tests use the real TS `server-starter` and LLMock as external processes (same as how dojo e2e works), and `AGUIChatClient` as the consumer.

```csharp
// AgenticChatTest.cs
public class AgenticChatTest : IClassFixture<TsServerFixture>
{
    private readonly TsServerFixture _fixture;

    public AgenticChatTest(TsServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ReceivesTextResponse_ForSimpleChat()
    {
        var client = new AGUIChatClient(_fixture.HttpClient, _fixture.AguiUrl);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hi, I am duaa"),
        };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages)
            .ConfigureAwait(false))
        {
            updates.Add(update);
        }

        var fullText = string.Join("", updates
            .Where(u => u.Text != null)
            .Select(u => u.Text));
        
        Assert.Matches("Hello.*duaa", fullText);
    }

    [Fact]
    public async Task ReceivesToolCall_ForBackendTool()
    {
        var client = new AGUIChatClient(_fixture.HttpClient, _fixture.AguiUrl);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "stock price of AAPL"),
        };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages)
            .ConfigureAwait(false))
        {
            updates.Add(update);
        }

        var fullText = string.Join("", updates
            .Where(u => u.Text != null)
            .Select(u => u.Text));
        
        Assert.Contains("150.25", fullText);
    }
}
```

```csharp
// TsServerFixture.cs — manages LLMock + TS server as real processes
public class TsServerFixture : IAsyncLifetime
{
    private Process? _llmockProcess;
    private Process? _tsServerProcess;
    public HttpClient HttpClient { get; private set; } = null!;
    public string AguiUrl => "http://localhost:5100/agui";

    public async Task InitializeAsync()
    {
        // Start LLMock on port 5555 (same as dojo e2e aimock-setup.ts)
        _llmockProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "node",
            Arguments = "llmock-server.mjs",
            WorkingDirectory = GetScriptsPath(),
            Environment = { ["PORT"] = "5555" },
        });
        await WaitForHealthy("http://localhost:5555/v1/models");

        // Start TS server-starter on port 5100 (pointed at LLMock)
        _tsServerProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "node",
            Arguments = "ts-server.mjs",
            WorkingDirectory = GetScriptsPath(),
            Environment =
            {
                ["PORT"] = "5100",
                ["OPENAI_BASE_URL"] = "http://localhost:5555/v1",
                ["OPENAI_API_KEY"] = "mock-key",
            },
        });
        await WaitForHealthy("http://localhost:5100/health");

        HttpClient = new HttpClient();
    }

    public Task DisposeAsync()
    {
        _tsServerProcess?.Kill();
        _llmockProcess?.Kill();
        HttpClient?.Dispose();
        return Task.CompletedTask;
    }

    private static async Task WaitForHealthy(string url, int timeoutSeconds = 30)
    {
        using var http = new HttpClient();
        for (int i = 0; i < timeoutSeconds; i++)
        {
            try
            {
                var response = await http.GetAsync(url).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return;
            }
            catch { }
            await Task.Delay(1000).ConfigureAwait(false);
        }
        throw new TimeoutException($"Server at {url} did not become healthy");
    }
}
```

### Part 3: C# Server in the Dojo Framework

The C# server (`AGUIDojoServer`) is **already integrated** into the dojo framework:
- `apps/dojo/scripts/run-dojo-everything.js` starts it: `dotnet run --project AGUIDojoServer.csproj --urls "http://localhost:8016"`
- `apps/dojo/src/agents.ts` maps routes: `agentic_chat`, `shared_state`, `human_in_the_loop`, `backend_tool_rendering`, `predictive_state_updates`, `subgraphs`, etc.
- Route prefix: `/microsoft-agent-framework-dotnet/feature/{feature}`

What needs to happen:
1. **Ensure `OPENAI_BASE_URL`** is set to `http://localhost:5555/v1` when running under dojo-everything (LLMock)
2. **Verify the C# server uses `OpenAIChatClient`** from `Microsoft.Extensions.AI.OpenAI` (which respects the base URL override)
3. **Reuse existing aimock fixtures** (`agentic-chat.json`, `shared-state.json`, `human-in-the-loop.json`) — the C# server calls the same OpenAI API shape
4. **Expand Playwright specs** in `microsoftAgentFrameworkDotnetTests/` to cover all scenarios

## Test Scenarios

| Scenario | TS Client → C# Server | C# Client → TS Server |
|---|---|---|
| Simple text chat ("Hi, I am duaa") | ✅ Verify text events stream correctly | ✅ Verify `ChatResponseUpdate.Text` |
| Multi-turn conversation | ✅ Context preserved across turns | ✅ Messages array forwarded |
| Backend tool call (stock price) | ✅ Tool call + result events emitted | ✅ Tool results in response |
| Frontend tool call (change_background) | ✅ Client-side tool invoked | ✅ Tool call detected, result sent back |
| Shared state (recipe) | ✅ STATE_SNAPSHOT + STATE_DELTA events | ✅ State updates received |
| Human-in-the-loop (approval) | ✅ Interrupt event → approval → resume | ✅ Interrupt handled programmatically |
| Reasoning/thinking | ✅ Reasoning events before text | ✅ Reasoning content received |
| Custom events | ✅ Passed through to client | ✅ Custom event deserialized |

## What This Validates (E2E, not just wire format)

- The C# server **transforms LLM completions into AG-UI events** identically enough that the TS client renders the same UI
- The C# client **interprets AG-UI events from TS servers** correctly and surfaces the same information
- **LLM tool-calling round trips** work across language boundaries (client registers tools → server invokes them → results flow back)
- **State management** (snapshot + JSON Patch deltas) is compatible across implementations
- **Interrupt/resume flows** work when server and client are different languages
- **Chunked streaming** doesn't break across implementations (different buffering behavior is OK as long as the final result is the same)

## Implementation Plan

### Phase 1: TS Client → C# Server (Vitest, headless)

This is the simpler direction — no browser needed, just `HttpAgent` + raw event assertions.

1. Create `sdks/dotnet/tests/cross-language/` Node project
2. Add `@ag-ui/client`, `@ag-ui/core`, `@copilotkit/aimock` as dependencies
3. Write `dotnet-server.ts` (start C# server as child process with `OPENAI_BASE_URL=http://localhost:5555/v1`)
4. Write `llmock-setup.ts` (start LLMock with existing fixtures from `apps/dojo/e2e/fixtures/openai/`)
5. Write Vitest tests that use `HttpAgent` against the C# server
6. Verify events match expectations (types, content, tool calls)

### Phase 2: C# Client → TS Server (xUnit)

1. Create `sdks/dotnet/tests/AGUI.CrossLanguage.IntegrationTests/` project
2. Write `TsServerFixture.cs` (manages LLMock + TS server child processes)
3. Write test classes per scenario using `AGUIChatClient`
4. Build a small Node script that bundles LLMock + server-starter into a single launchable process
5. Verify `ChatResponseUpdate` output matches expectations

### Phase 3: Expand Dojo E2E (Playwright, browser)

The C# server is **already registered** in the dojo framework (`run-dojo-everything.js` starts it on port 8016, `agents.ts` maps all routes). What's needed:

1. Ensure the C# server uses `OPENAI_BASE_URL=http://localhost:5555/v1` so LLMock handles its AI calls
2. Expand `microsoftAgentFrameworkDotnetTests/` Playwright specs to cover all features (`shared_state`, `human_in_the_loop`, `backend_tool_rendering`, `predictive_state_updates`, etc.)
3. The specs should be identical to `serverStarterAllFeaturesTests/` — same user interactions, same expected UI results
4. If the C# server produces the same UI rendering as the TS server-starter, interop is proven end-to-end

### Phase 4: CI

1. Add GitHub Actions job for Phase 1 (Vitest, needs .NET SDK + Node)
2. Add GitHub Actions job for Phase 2 (xUnit, needs .NET SDK + Node)
3. Phase 3 runs as part of existing dojo e2e CI (just adds another backend)

## Key Decisions

| Decision | Rationale |
|---|---|
| Use `@copilotkit/aimock` (LLMock) | Same mock infrastructure as all other dojo tests; tests real LLM→AG-UI transformation, not just wire format |
| Reuse existing fixtures (`apps/dojo/e2e/fixtures/openai/`) | Same prompts/responses as existing tests; if TS server passes, C# server should produce same end result |
| Phase 1 (Vitest) before Phase 3 (Playwright) | Headless HTTP-level tests are faster to write and debug; Playwright adds browser/React complexity |
| All servers run as real processes | Real TCP, real serialization, real SSE framing — same as how dojo runs all backends; no in-process test hosts |
| `AGUIChatClient` as the C# client abstraction | This is the `IChatClient` implementation that maps AG-UI events to `ChatResponseUpdate` — the idiomatic .NET consumer |
| All new code in `sdks/dotnet/tests/` | .NET team owns the cross-language validation; no changes needed to `sdks/typescript/` or `apps/dojo/` for Phases 1-2 |

## Dependencies

- `@ag-ui/client` and `@ag-ui/core` — consumed as npm packages (workspace link or published version)
- `@copilotkit/aimock` — LLMock server
- `dotnet` CLI — to build and run C# server/client
- The C# server must support `OPENAI_BASE_URL` environment variable to point at LLMock
- Node.js 20+ for running LLMock and TS server

## Success Criteria

1. TS `HttpAgent` can consume all scenarios from the C# server and receives correct events
2. C# `AGUIChatClient` can consume all scenarios from the TS server and produces correct `ChatResponseUpdate` output
3. Both directions pass with the **same LLMock fixtures** — proving the servers are functionally equivalent from the client's perspective
4. Tests run in CI on every PR that touches `sdks/dotnet/`
5. Any behavioral regression (not just wire format) is caught before merge
