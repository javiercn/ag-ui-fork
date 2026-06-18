# AG-UI .NET SDK — Changes Since the Initial Implementation

This document summarizes, at a high level, the work done to clean up the AG-UI
.NET SDK contribution ([PR #1963](https://github.com/ag-ui-protocol/ag-ui/pull/1963))
**after** the initial implementation commits, in preparation for undrafting.

## Baseline

The initial implementation was the bottom ~10 commits on the
`dotnet-support-contribution` branch (`d89fe7ef` scaffold → `5efe84c0`), which
added the solution, the wire format (`AGUI.Abstractions`), the `AGUI.Client`
and `AGUI.Hosting.AspNetCore` packages, the GettingStarted samples, the
cross-language test harness, and a few extra samples.

Everything below was added afterwards: **60 commits** touching `sdks/dotnet`
(398 files), plus supporting changes in `.github/` and `apps/dojo/`. The large
insertion count is dominated by recorded LLM fixtures/baselines.

## 1. Sample cleanup & removal

- Removed the wrong/unneeded samples: `AGUIWithMcpTools` and `AGUIWithCopilotSdk`.
- Made `AGUIDojoServer` self-contained (dropped agent-framework workflow bits).

## 2. Sample restructuring — consistent Server + Client split

- Split every `Step01`–`Step11` sample into separate **Server** and **Client**
  projects sharing a single `SampleClient` scenario, so each sample reads like
  idiomatic user code.
- Wrapped client `Program.cs` files to avoid `Program` type collisions across
  projects.

## 3. Native AG-UI SDK feature work (driven by the sample reworks)

- Native handling of `TextReasoningContent`,
  `InterruptRequestContent` / `InterruptResponseContent`, and AG-UI `RawEvents`
  in `AsAGUIEventStreamAsync`.
- Step04 pre-interrupt approval pattern via paired wrappers.
- Step09 native tool-approval interrupt round-trip (`ToolApprovalResumeChatClient`),
  collapsed to a canonical endpoint.
- Step10 rewritten around native `InterruptRequest`/`InterruptResponse` contents.
- Added `AGUIConstants.RunAgentInputKey`; consolidated internal
  `AdditionalProperties` keys; `RawRepresentationFactory` for `parentRunId`
  branching; dropped a redundant `agui_interrupt` stash.

## 4. Real-LLM record/replay for samples & integration tests

- Captured real LLM runs (gpt-4o / gpt-5-mini, including the Responses API for
  reasoning) for Steps 02, 07, 08, 09, and 10 — replacing synthetic data with
  deterministic replay backed by validated, protocol-compliant output.
- Captured **full protocol data** in integration-test baselines; split baselines
  into **per-capture-point** files; moved replay recordings into a `fixtures/`
  folder; moved shared test code into `Infrastructure/`.
- Samples now support **Azure OpenAI (Entra ID)** and OpenAI-compatible endpoints.

## 5. Frontend-tool correctness fixes

- Fixed the frontend-tool continuation to send valid `tool_calls` to the LLM.
- Re-surface frontend tools freshly when the model re-invokes them, so a
  re-invocation does not return a stale result.

## 6. Mixed client + server tool invocation

- New `MixedToolInvocationIntegrationTest`, real-LLM-backed, with full
  8-capture-point baselines.
- Cross-language mixed test (TS client → C# server) proving the mixed dance is
  transparent to the client: the client receives a `TOOL_CALL` for both tools,
  executes only its own tool, and echoes both calls plus its result back; the C#
  server resolves the server tool transparently.

## 7. Cross-language test harness (TS client ↔ C# server)

- Ported **all** `Step01`–`Step11` scenarios to TS-driven Vitest tests that run
  against the C# server via AIMock.
- Added **real-LLM record/replay** to the cross-language harness: AIMock proxies
  unmatched LLM calls to Azure (`/openai/v1`), forwarding an Entra ID (AAD)
  bearer token, captures the responses as fixtures, and replays them offline and
  deterministically — no C# server changes required.
- Used AIMock's idiomatic **tool-result matching** (`toolCallId` / `predicate`
  on the last `role:"tool"` message) for multi-turn flows instead of
  `sequenceIndex`, making fixtures turn-count-independent.
- Fixed Step06 RAW telemetry emission so the standalone server demonstrates raw
  events end-to-end (the no-handler fallback now emits a `UsageContent`).
- Run cross-language test files sequentially for stability.

## 8. CI, dojo integration & docs

- New `unit-dotnet-sdk` GitHub Actions workflow; dojo e2e wiring.
- Added the AG-UI .NET SDK as a **dojo integration** (`apps/dojo`: agents, menu,
  files, Playwright spec).
- Authored `cross-language-testing.md` (topology, record/replay workflow,
  multi-turn guidance) and an `agui-dotnet-integration-tests` skill doc.

## Status

- Cross-language Vitest suite: **26/26 green** on offline replay.
- .NET integration suite green; samples back their assertions with real,
  recorded LLM output.
