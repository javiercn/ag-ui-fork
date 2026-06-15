# AGUIWithCopilotSdk

A two-project sample — server + console client — that bridges the stateful [GitHub Copilot SDK for .NET](https://github.com/github/copilot-sdk/tree/main/dotnet) to AG-UI's stateless wire contract.

| Project | Role |
|---|---|
| [`AGUIWithCopilotSdkServer/`](./AGUIWithCopilotSdkServer/) | ASP.NET Core AG-UI host. Maps each AG-UI ThreadId to a long-lived `CopilotSession`, translates session events into AG-UI events. |
| [`AGUIWithCopilotSdkConsoleClient/`](./AGUIWithCopilotSdkConsoleClient/) | Console app that drives a three-turn conversation over a single ThreadId via `AGUIChatClient`, proving the session is truly stateful (turn 3 recalls what was said in turn 1). |

## What it shows

AG-UI is a stateless protocol — every `POST /agui` carries the full message history in `RunAgentInput.Messages`. The GitHub Copilot SDK is fundamentally stateful: each `CopilotSession` owns its own conversation history server-side and expects you to send only the new user prompt on each turn.

The server bridges the two:

| AG-UI concept | Copilot concept |
|---|---|
| `RunAgentInput.ThreadId` | `CopilotSession.SessionId` (1-to-1, persisted across process restarts via `ResumeSessionAsync`) |
| `RunAgentInput.RunId` | The single Copilot turn issued by `session.SendAsync` |
| `RunAgentInput.Messages[-1]` (last user) | The `Prompt` field of `MessageOptions` |
| `AssistantMessageStartEvent` | AG-UI `TEXT_MESSAGE_START` |
| `AssistantMessageDeltaEvent.Data.DeltaContent` | AG-UI `TEXT_MESSAGE_CONTENT` |
| `SessionIdleEvent` | AG-UI `TEXT_MESSAGE_END` + `RUN_FINISHED` |

The full sequencing happens in [`CopilotAGUIEndpoint.cs`](./AGUIWithCopilotSdkServer/CopilotAGUIEndpoint.cs); the per-thread session lookup with disk-backed resume happens in [`CopilotSessionRegistry.cs`](./AGUIWithCopilotSdkServer/CopilotSessionRegistry.cs).

## Why this pattern is worth knowing

Stateful agent runtimes (Copilot CLI, persistent LangGraph state, conversational planners, …) don't naturally map to AG-UI's "send the full history every turn" contract. Trying to replay AG-UI history into a stateful session would duplicate messages and break the agent's internal turn-tracking. The right answer is:

1. **Map ThreadId 1:1 to a long-lived stateful session.**
2. **Only send the new turn**, not the full history.
3. **Convert the session's event stream** into the AG-UI event family on the wire.
4. **Persist the session ID across restarts** so the runtime can resume from disk.

## Prerequisites

- .NET 10 SDK
- GitHub authentication — either `gh auth login` (the default `UseLoggedInUser=true` mode) **or** a `GITHUB_TOKEN` environment variable
- Network access on first build; the bundled Copilot CLI runtime (~90 MB) is downloaded automatically by the `GitHub.Copilot.SDK` NuGet package

## Running

Start the server (defaults to `http://localhost:5019`):

```sh
cd sdks/dotnet/samples/AGUIWithCopilotSdk/AGUIWithCopilotSdkServer
dotnet run
```

In another terminal, run the console client:

```sh
cd sdks/dotnet/samples/AGUIWithCopilotSdk/AGUIWithCopilotSdkConsoleClient
dotnet run
```

Output: three turns of conversation over a single auto-generated ThreadId — the assistant should remember the user's name in turn three even though only the new prompt is sent on each call.

To resume the same thread across restarts (Copilot persists sessions to disk under `~/.copilot`):

```sh
dotnet run -- http://localhost:5019 thread-20260608120000
```

## Configuration

| Setting | Effect |
|---|---|
| `CopilotModel` | Override the model passed to `SessionConfig.Model` (default: SDK default — currently `claude-sonnet-4.5`). Set via env var `CopilotModel=gpt-5` or appsettings. |
| `GITHUB_TOKEN` env var | Use a PAT instead of the user's `gh auth` credentials. |
