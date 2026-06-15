# Cross-language E2E tests: TypeScript ↔ .NET

This directory implements the [cross-language testing plan](../cross-language-testing.md) — exercising the .NET AG-UI SDK against the TypeScript AG-UI client over the wire, and vice versa, end-to-end.

## Layout

| Path | What it is |
|---|---|
| `CrossLanguage.TestServer/` | Minimal C# AG-UI server that consumes `OPENAI_BASE_URL`. Built on the local `AGUI.Abstractions` + `AGUI.Hosting.AspNetCore` project references — exercises *our* code, not the published NuGet packages. Hosts `/agentic_chat` and `/backend_tool_rendering`. |
| `CrossLanguage.Vitest/` | Phase 1: TypeScript `HttpAgent` (from `@ag-ui/client`) drives the C# server above. LLMock (`@copilotkit/aimock`) supplies the upstream LLM responses. The `server/` subdirectory also contains a fake-agent TS HTTP server used by Phase 2. |
| `AGUI.CrossLanguage.IntegrationTests/` | Phase 2: C# `AGUIChatClient` drives the fake-agent TS server (`CrossLanguage.Vitest/server/main.ts`). The TS server emits canned AG-UI events via `@ag-ui/encoder`, mirroring the in-memory `class FooAgent extends AbstractAgent` pattern the TS SDK's own tests use. |

## Why two TS servers (LLMock-backed vs fake-agent)

The TS SDK ships no reference HTTP server — the only runnable AG-UI servers in the repo are integration packages (aws-strands, claude-agent-sdk, etc.). Rather than depend on an integration, the cross-language tests use two purpose-built servers:

- **Phase 1's "real" server is the C# one** (`CrossLanguage.TestServer`) — it makes actual OpenAI-shaped calls which LLMock answers from JSON fixtures. This verifies the C# server's LLM → AG-UI translation pipeline against the real TS client.
- **Phase 2's "real" server is the TS fake-agent** (`CrossLanguage.Vitest/server/main.ts`) — it skips the LLM entirely and emits canned AG-UI events directly via `@ag-ui/encoder`, just like every TS SDK test does with `class TestAgent extends AbstractAgent { run() { return of(...events); } }`. This verifies the C# client correctly consumes real TS-encoded AG-UI events.

## Prerequisites

- .NET 10 SDK
- Node.js 20+ and pnpm 10+ (the repository's `packageManager`)
- `pnpm install` from the repository root (one-off)

## Running

### Phase 1 (TS client → C# server) — Vitest

```sh
cd sdks/dotnet/tests/CrossLanguage.Vitest
pnpm test
```

`helpers/global-setup.ts` starts LLMock on :5556, builds and spawns `CrossLanguage.TestServer.exe` on :8091 with `OPENAI_BASE_URL=http://localhost:5556/v1`, waits for the HTTP listener, then runs the test files.

### Phase 2 (C# client → TS server) — xUnit

```sh
cd sdks/dotnet/tests/AGUI.CrossLanguage.IntegrationTests
dotnet test
```

`TsServerFixture` shells out `pnpm run server` (which runs `tsx server/main.ts` in the Vitest project) to start the fake-agent server on :8092, then drives it with `AGUIChatClient`.

### Manually starting the TS fake-agent server

For ad-hoc debugging:

```sh
cd sdks/dotnet/tests/CrossLanguage.Vitest
pnpm run server          # listens on :8092
curl -X POST http://localhost:8092/agentic_chat \
  -H 'Content-Type: application/json' \
  -d '{"threadId":"t","runId":"r","messages":[{"id":"u","role":"user","content":"Hi"}],"tools":[],"context":[],"state":{},"forwardedProps":{}}'
```

## Adding a scenario

**Phase 1** (more code per scenario, but full LLM pipeline):
1. Add a fixture in `CrossLanguage.Vitest/fixtures/` matched by `userMessage` / `toolName` / `predicate` (`@copilotkit/aimock` syntax).
2. If the scenario needs a new route or new server-side tool, add it to `CrossLanguage.TestServer/` and `Program.cs`.
3. Add a `*.test.ts` under `CrossLanguage.Vitest/tests/`.

**Phase 2** (no LLM, faster, deterministic):
1. Add a fake agent in `CrossLanguage.Vitest/server/fakeAgents.ts` (a function `(RunAgentInput) => BaseEvent[]`).
2. Mount the route in `CrossLanguage.Vitest/server/main.ts`.
3. Add a `*.cs` test file in `AGUI.CrossLanguage.IntegrationTests/`, decorated `[Collection(nameof(TsServerCollection))]`.

## Windows process cleanup

Both directions use a port-based fallback (`netstat -ano | taskkill /F /PID`) to clean up server processes that Node's `child.kill()` or .NET's `Process.Kill(entireProcessTree)` couldn't reach. Without this, an orphan server keeps the parent shell's stdout pipe alive and makes `dotnet test` / `pnpm test` appear to hang indefinitely after the tests have already passed.
