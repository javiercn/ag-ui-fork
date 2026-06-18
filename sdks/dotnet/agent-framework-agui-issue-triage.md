# microsoft/agent-framework — AG-UI / .NET Open-Issue Triage

Analysis of the **53 open** issues in [microsoft/agent-framework](https://github.com/microsoft/agent-framework)
labeled `.NET` + `ui integration` (the AG-UI label). Each issue was read in full
(including comments) and cross-referenced against our standalone AG-UI .NET SDK
(`sdks/dotnet/src`) — the three packages Agent Framework intends to consume:
`AGUI.Abstractions`, `AGUI.Client`, `AGUI.Hosting.AspNetCore`.

Collected: 2026-06-17.

## Classification key

- **Integration-pkg** — core AG-UI protocol/mapping behavior our packages own; Agent
  Framework gets the fix/feature simply by consuming our packages. **(22 issues)**
- **Deprecated-workaround** — the reported behavior relied on a MEAI smuggling workaround
  (notably `DataContent` carrying `application/json` that the in-tree transport magically
  converted to `STATE_SNAPSHOT`/`STATE_DELTA`). That existed only because the AG-UI event
  types were **internal** (#2558). Our SDK makes them public, so the workaround is
  **deprecated in favor of emitting the proper AG-UI event directly**. **(2 issues)**
- **AF-specific** — depends on Agent-Framework specifics our packages do not own (DevUI,
  Workflows/WorkflowAsAgent, Aspire, multi-agent/handoffs, Copilot Studio, Azure AI
  Projects agent-mode, session stores, agent resolution). **(19 issues)**
- **Not-AGUI** — not about the AG-UI wire protocol (pure DevUI UX/docs/tooling, MCP-UI). **(10 issues)**

## 🔁 Deprecated workarounds — use the now-public AG-UI concepts

The in-tree Agent Framework AG-UI implementation kept the AG-UI event types **internal**
(#2558), so developers smuggled AG-UI concepts through MEAI content — notably
`DataContent("application/json")`, which the transport magically converted into
`STATE_SNAPSHOT`/`STATE_DELTA`. Our SDK makes `StateSnapshotEvent`, `StateDeltaEvent`,
`CustomEvent`, and `RawEvent` **public**, and `AsAGUIEventStreamAsync` emits **any**
`BaseEvent` set as `ChatResponseUpdate.RawRepresentation` directly
(`ChatResponseUpdateAGUIExtensions.cs:66`). These workarounds are therefore deprecated:

| Issue(s) | Deprecated workaround | Deprecated in favor of (X) |
| --- | --- | --- |
| #2081 | Emit tool/agent state as `DataContent("application/json")` → magic `STATE_SNAPSHOT` | Emit `StateSnapshotEvent` / `StateDeltaEvent` directly via `RawRepresentation` (now public) |
| #4635 | Carry app data + metadata (e.g. `DataContent.Name`) over AG-UI as `DataContent` | Emit `CustomEvent` / `StateSnapshotEvent` / `RawEvent` directly via `RawRepresentation` |
| #4177 (partial) | Manual middleware emitting `DataContent` from `StateBag` mutations | Emit state events directly; the *auto-bridge* from AF `StateBag` stays AF-specific |

> **Do not** port the `DataContent → STATE_*` magic mapping into our packages — that would
> re-introduce the workaround. Keep/document the direct event-emission path instead.

## ⚠️ Priority — genuine gaps likely present in OUR implementation

Real mapping gaps (not workarounds) to address before undrafting:

| Issue(s) | Gap | Where |
| --- | --- | --- |
| #3790 | `RunFinishedEvent` always emits success; `ChatFinishReason` (e.g. `tool_calls`) dropped, no `FinishReason` field | `ChatResponseUpdateAGUIExtensions` (~line 379), `RunFinishedEvent` |
| #2699 | Parallel tool-calls split across assistant messages aren't coalesced → invalid OpenAI tool_call history | `AGUIChatMessageExtensions.AsChatMessages()` |
| #3723 | No client-side OpenTelemetry: no W3C trace-context injection, no `ActivitySource` span | `AGUIHttpTransport`, `AGUIChatClient` |
| #6479 | Client tool calls that never return can break persisted history; no synthetic `FunctionResultContent` fallback | `RunAgentInputExtensions` / hosting |
| #4869 | `AGUIChatClient` returns `ConversationId` despite being stateless, misleading `AsAIAgent` into delta-only turns | `AGUIChatClient` (~line 173) |
| #3962, #3475 | Low-risk: `messageId == toolCallId` reuse for TOOL_CALL_RESULT (semantic mismatch); audit `agui_thread_id` key consistency | `AGUIEventExtensions`, `AGUIClientInternalKeys` |

## 🤔 Design decision — usage reporting (#3520, #3684, #3752)

`UsageContent` from the model isn't mapped to any AG-UI event (it falls to the unmapped
`default` case). AG-UI has no standard usage event, so the idiomatic public-API path is to
emit a `CustomEvent` (or `RawEvent`) carrying usage via `RawRepresentation`. Decide whether
our hosting should **auto-emit** such a `CustomEvent` for `UsageContent`, or document the
manual path. (Not a `DataContent`-style workaround, but enabled by the same public-event mechanism.)

## ✅ Already fixed / covered in our SDK (verify + add test; AF gains by consuming)

#5567 (STATE_DELTA deserialization), #4342 & #3365 (ToolMessage `MessageId`), #2637
(`parentMessageId` null omitted), #3729 (multimodal content arrays), #2558 (event types now
**public** — the enabler for the deprecations above), #5209 (conversion API public), #2510
(`MessagesSnapshotEvent`), #5587 & #6511 (non-JSON TOOL_CALL_RESULT content), #3769 (transport
already decoupled).

## Full table

| Issue | Title | AG-UI? | Classification | Our-impl risk note | Proposed step |
|-------|-------|--------|----------------|--------------------|---------------|
| [#6519](https://github.com/microsoft/agent-framework/issues/6519) | Make AG-UI hosting (MapAGUI) transport-extensible – currently SSE-only | Yes | Integration-pkg | No server-side transport abstraction yet; only `AsAGUIEventStreamAsync` stream primitives. Client has `IAGUITransport`. | Add server-side `IAGUIServerTransport` + default SSE `MapAGUI`. |
| [#6511](https://github.com/microsoft/agent-framework/issues/6511) | Tool Call Error in AGUI Mapping (WorkflowAsAgent with Session) | Partial | Integration-pkg | Our `EventStreamConverter` passes tool result content as a string, no extra JSON re-parse (AF's bug avoided). | Confirm AF consumes our `AGUI.Client`; bug disappears. |
| [#6479](https://github.com/microsoft/agent-framework/issues/6479) | Frontend tools break persisted chat history | Partial | Integration-pkg | Client-tool `FunctionCallContent` with no result can corrupt persisted history; no synthetic result fallback. | Add synthetic `FunctionResultContent` fallback in `RunAgentInputExtensions`. |
| [#6006](https://github.com/microsoft/agent-framework/issues/6006) | DEVUI Read unrecognized discriminator 'function_approval_response' | Partial | AF-specific | Root cause in AF's OpenAI Responses `ItemParamConverter`; we own no Responses converters. | AF adds `FunctionApprovalResponseItemParam` case. |
| [#5891](https://github.com/microsoft/agent-framework/issues/5891) | DevUI: edit, regenerate, rerun support for messages | No | Not-AGUI | N/A — DevUI UX feature. | Close as DevUI scope. |
| [#5806](https://github.com/microsoft/agent-framework/issues/5806) | DevUI in Aspire has no OpenTelemetry visibility | No | Not-AGUI | N/A — Aspire DevUI telemetry wiring. | Route to Aspire+DevUI team. |
| [#5781](https://github.com/microsoft/agent-framework/issues/5781) | Aspire DevUI README → "Agent 'agentservice' not found" | No | Not-AGUI | N/A — `WithAgentService()` defaults entity-id to resource name. | Fix `WithAgentService()` default. |
| [#5779](https://github.com/microsoft/agent-framework/issues/5779) | Aspire DevUI README → 404 Not Found | No | Not-AGUI | N/A — missing transitive dep on `Microsoft.Agents.AI.DevUI`. | Add PackageReference in Aspire DevUI csproj. |
| [#5614](https://github.com/microsoft/agent-framework/issues/5614) | Convert Hosting.AGUI.AspNetCore to .NET Standard | Partial | AF-specific | Same limitation: ASP.NET Core can't target netstandard2.0; Abstractions/Client can multi-target. | Multi-target Abstractions/Client; document hosting is net8+. |
| [#5607](https://github.com/microsoft/agent-framework/issues/5607) | Request for .NET DevUI Documentation & Integration Guide | No | Not-AGUI | N/A — DevUI docs. | Close as dup of #1283; link DevUI README. |
| [#5587](https://github.com/microsoft/agent-framework/issues/5587) | AGUI client crashes on non-JSON TOOL_CALL_RESULT content | Yes | Integration-pkg | Not reproduced: `EventStreamConverter` keeps result content as string, no JSON parse. | Add plain-text tool-result test. |
| [#5567](https://github.com/microsoft/agent-framework/issues/5567) | BaseEventJsonConverter.Read missing StateDeltaEvent mapping | Yes | Integration-pkg | Already fixed — `BaseEventJsonConverter` has the `StateDelta` case. | Confirm AF replaces in-tree converter. |
| [#5209](https://github.com/microsoft/agent-framework/issues/5209) | Make AG-UI conversion API public for multi-agent orchestrations | Yes | Integration-pkg | Already public: events, `AGUIEventTypes`, `AsAGUIEventStreamAsync`, `MessagesSnapshotEvent`. | Inform AF our packages already expose these. |
| [#5203](https://github.com/microsoft/agent-framework/issues/5203) | Add a session when initiating an agent conversation in DevUI | No | Not-AGUI | N/A — DevUI session-store plumbing; we have no `Session` concept. | AF wires `AgentSession` into DevUI history provider. |
| [#4920](https://github.com/microsoft/agent-framework/issues/4920) | MapAGUI() doesn't use session store or map message AuthorName | Partial | AF-specific | `AuthorName`↔`Name` already mapped both directions; stateless full-history is by design. | Document stateless design; AF wires session store. |
| [#4902](https://github.com/microsoft/agent-framework/issues/4902) | Events should make clear which agent is executing (multi-agent) | Partial | AF-specific | We already pass `AuthorName`→`name` on `TextMessageStart`; AF workflow must set `AuthorName`. | Verify AF sets `AuthorName` per-agent. |
| [#4869](https://github.com/microsoft/agent-framework/issues/4869) | AGUIChatClient sets ConversationId despite being stateless | Yes | Integration-pkg | Returns `ConversationId` on every update, misleading `AsAIAgent` into delta-only turns. | Clarify/fix `ConversationId` semantics; update wrappers. |
| [#4825](https://github.com/microsoft/agent-framework/issues/4825) | GitHubCopilotAgent should translate permission.requested → HITL | Partial | AF-specific | N/A — needs `GitHubCopilotAgent`-specific translation we don't own. | AF wires Copilot permission events through interrupt pipeline. |
| [#4635](https://github.com/microsoft/agent-framework/issues/4635) | Not all DataContent properties propagated server→client | Yes | Deprecated-workaround | `DataContent`-as-AG-UI-payload (e.g. `Name`) was a smuggling workaround for internal event types. Don't add a DataContent mapping. | Deprecated → emit `CustomEvent`/`StateSnapshotEvent`/`RawEvent` via `RawRepresentation` (public). |
| [#4342](https://github.com/microsoft/agent-framework/issues/4342) | AGUIToolMessage→ChatMessage loses MessageId | Yes | Integration-pkg | Already fixed — `AGUIChatMessageExtensions` sets `MessageId = message.Id`. | Add ToolMessage MessageId round-trip test. |
| [#4177](https://github.com/microsoft/agent-framework/issues/4177) | Auto-translate StateBag mutations + streaming tool-args to state events | Partial | AF-specific | Manual `DataContent` workaround superseded by direct `StateSnapshotEvent`/`StateDeltaEvent` emission (now public); the auto-bridge from AF `StateBag` remains AF-specific. | AF adds `StateBag` observer emitting state events directly (no DataContent). |
| [#3975](https://github.com/microsoft/agent-framework/issues/3975) | DevUI returning 401 execution_error (Azure AI Projects) | No | Not-AGUI | N/A — Azure AI Projects agent-mode auth, not protocol mapping. | Investigate AF Azure AI Projects auth pipeline. |
| [#3962](https://github.com/microsoft/agent-framework/issues/3962) | MapAGUI reuses same messageId for consecutive TOOL_CALL_RESULT | Yes | Integration-pkg | Low risk: we set `messageId = toolCallId` (unique), but semantically messageId ≠ toolCallId. | Verify uniqueness test; optionally generate fresh messageId. |
| [#3823](https://github.com/microsoft/agent-framework/issues/3823) | [Workflows] Session always null in middleware | Partial | AF-specific | N/A — session flows through AF `WorkflowBuilder`/`AIHostAgent`, untouched by us. | AF debugs session flow in `WorkflowBuilder.AsAgent()`. |
| [#3790](https://github.com/microsoft/agent-framework/issues/3790) | AG-UI hosting drops FinishReason on RunFinishedEvent | Yes | Integration-pkg | **Same gap**: always emits success outcome; `FinishReason` not captured/exposed. | Capture `FinishReason`; expose via `RunFinishedEvent`/new outcome. |
| [#3769](https://github.com/microsoft/agent-framework/issues/3769) | Decouple AG-UI Protocol from Transport | Yes | Integration-pkg | Already decoupled: `AsAGUIEventStreamAsync` returns `IAsyncEnumerable<BaseEvent>`; SSE is one wiring. | Publish a non-SSE (WebSocket/gRPC) sample. |
| [#3752](https://github.com/microsoft/agent-framework/issues/3752) | [AG-UI] Usage and Annotations are not present | Yes | Integration-pkg | `UsageContent` falls to `default`; no AG-UI usage event. Design decision, not a DataContent workaround. | Decide: auto-emit a `CustomEvent` for usage, or document emitting via public Custom/Raw event. |
| [#3729](https://github.com/microsoft/agent-framework/issues/3729) | ASP.NET Core endpoint crashes on multimodal user messages | Yes | Integration-pkg | Already fixed: `AGUIMessageJsonConverter` handles string + array content; maps binary→`DataContent`/`UriContent`. | Add multimodal integration test. |
| [#3723](https://github.com/microsoft/agent-framework/issues/3723) | OpenTelemetry not emitted when Agent triggered through AGUI | Yes | Integration-pkg | **Gap**: `AGUIHttpTransport` injects no W3C trace context; `AGUIChatClient` emits no span. | Inject trace context; add `ActivitySource` span. |
| [#3684](https://github.com/microsoft/agent-framework/issues/3684) | UsageContent not returned in AG-UI stream | Yes | Integration-pkg | Same as #3752: `UsageContent` not mapped to any AG-UI event. | Decide: auto-emit `CustomEvent` for usage, or document the public Custom/Raw path. |
| [#3520](https://github.com/microsoft/agent-framework/issues/3520) | Expose USAGE details (tokens) to the front end | Yes | Integration-pkg | `UsageContent` not mapped to an AG-UI event; surfaceable via public Custom/Raw event. | Decide: auto-emit a `CustomEvent` for usage, or document the public path. |
| [#3475](https://github.com/microsoft/agent-framework/issues/3475) | Inconsistent ag_ui_thread_id vs agui_thread_id keys | Yes | Integration-pkg | Low risk: client uses `agui_thread_id` consistently; hosting stashes whole input under `agui_input`. | Audit AdditionalProperties keys; document contract. |
| [#3365](https://github.com/microsoft/agent-framework/issues/3365) | AGUIToolMessage→ChatMessage MessageId is null | Yes | Integration-pkg | Already fixed — `MessageId = message.Id` set for ToolMessage. | Confirm AF adopts our extensions. |
| [#3215](https://github.com/microsoft/agent-framework/issues/3215) | Copilot Studio Agent and AG-UI Protocol Integration | Partial | AF-specific | N/A — root cause in `Microsoft.Agents.AI.CopilotStudio` `ActivityProcessor`. | AF fixes `ActivityProcessor`. |
| [#3033](https://github.com/microsoft/agent-framework/issues/3033) | .NET client FormatException on fractional created_at (Python DevUI) | No | AF-specific | N/A — AF's OpenAI Responses client timestamp parsing. | Fix AF Responses client `created_at` parse. |
| [#3011](https://github.com/microsoft/agent-framework/issues/3011) | Unable to test handoffs workflow using DevUI | No | AF-specific | N/A — workflow factory name mismatch in AF's `AddAsAIAgent()`. | Fix workflow name propagation. |
| [#3002](https://github.com/microsoft/agent-framework/issues/3002) | Workflow as AG-UI agent does not recognise client-side tools | Partial | AF-specific | Our `ToChatRequestContext` reads tools into `ChatOptions.Tools`; WorkflowAsAgent doesn't forward them. | Add test that client tools surface in `ChatRequestContext`. |
| [#2988](https://github.com/microsoft/agent-framework/issues/2988) | Support dynamic agent resolution in AG-UI endpoints | Partial | AF-specific | Our public building blocks already allow DIY dynamic endpoints; factory-`MapAGUI` is AF DI. | Document DIY dynamic-routing path. |
| [#2959](https://github.com/microsoft/agent-framework/issues/2959) | Azure AI Projects agent-mode silently ignores per-request tools | Partial | AF-specific | N/A — `AzureAIProjectChatClient` drops tools before the model. | AF adds per-request tool union/warning. |
| [#2911](https://github.com/microsoft/agent-framework/issues/2911) | DevUI freezes with sub-workflow as WorkflowAsAgent | No | AF-specific | N/A — AF Workflow engine + DevUI rendering. | Route to AF Workflow team. |
| [#2702](https://github.com/microsoft/agent-framework/issues/2702) | .NET ag-ui with OpenAI Responses API | Partial | AF-specific | Our `ToChatRequestContext` maps threadId→ConversationId; responseId tracking is AF session layer. | AF implements responseId↔threadId middleware. |
| [#2699](https://github.com/microsoft/agent-framework/issues/2699) | Multi-turn parallel tool calls replay → invalid OpenAI history | Yes | Integration-pkg | **Risk**: `AsChatMessages()` maps assistant messages 1:1; split parallel calls produce invalid history. | Coalesce adjacent single-tool-call assistant messages. |
| [#2691](https://github.com/microsoft/agent-framework/issues/2691) | DevUI workflows require complex Chat Protocol vs simple Python | No | AF-specific | N/A — `WorkflowOutputEvent` display gap in AF pipeline; no such concept in our packages. | AF handles `WorkflowOutputEvent` like Python mapper. |
| [#2637](https://github.com/microsoft/agent-framework/issues/2637) | parentMessageId serialized as null breaks @ag-ui/core | Yes | Integration-pkg | Already fixed — `[JsonIgnore(WhenWritingNull)]` on `ParentMessageId`. | Add test that null parentMessageId is omitted. |
| [#2558](https://github.com/microsoft/agent-framework/issues/2558) | Support more AG-UI event types | Yes | Integration-pkg | Already defined: Step*, MessagesSnapshot, Activity*, Raw, Custom; raw passthrough supported. | Confirm AOT serializer covers all; tell AF. |
| [#2555](https://github.com/microsoft/agent-framework/issues/2555) | Support MCP-UI Protocol | No | Not-AGUI | N/A — MCP-UI is a different protocol. | Out of scope; track separately. |
| [#2517](https://github.com/microsoft/agent-framework/issues/2517) | Thread persistence does not work by default in MapAGUI | Partial | AF-specific | We surface `ThreadId` via `ChatOptions.AdditionalProperties`; wiring to `AgentThread`/store is AF. | Document threadId availability; AF wires store. |
| [#2510](https://github.com/microsoft/agent-framework/issues/2510) | Sync AG-UI conversation history from backend | Yes | Integration-pkg | Already supported: `MessagesSnapshotEvent` implemented + public + serialized. | Document `MessagesSnapshotEvent` usage. |
| [#2494](https://github.com/microsoft/agent-framework/issues/2494) | AG-UI support for workflow as agent | Partial | AF-specific | N/A — `WorkflowAsAgent`/workflow events are AF-specific; we map `ChatResponseUpdate` only. | AF adds `WorkflowAsAgent`→`IChatClient` adapter. |
| [#2179](https://github.com/microsoft/agent-framework/issues/2179) | Pass Agent Name (string) instead of AIAgent to MapAGUI | Partial | AF-specific | N/A — needs AF DI agent registry; our stream primitives are agent-agnostic. | Add `MapAGUI(string agentName)` overload in AF. |
| [#2109](https://github.com/microsoft/agent-framework/issues/2109) | DevUI Structured Input support | No | Not-AGUI | N/A — DevUI dev tooling. | No action in our packages. |
| [#2084](https://github.com/microsoft/agent-framework/issues/2084) | DevUI limitation should be documented and addressed | No | Not-AGUI | N/A — DevUI workflow executor/visualization. | Track in DevUI backlog. |
| [#2081](https://github.com/microsoft/agent-framework/issues/2081) | Support intermediate state from Tools for AG-UI | Yes | Deprecated-workaround | `DataContent("application/json")`→`STATE_SNAPSHOT` smuggling existed because the state events were internal. | Deprecated → emit `StateSnapshotEvent`/`StateDeltaEvent` directly via `RawRepresentation` (public). |
