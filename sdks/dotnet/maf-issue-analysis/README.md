# MAF AG-UI Issue Analysis

Per-issue analysis of the **53 open** [microsoft/agent-framework](https://github.com/microsoft/agent-framework)
issues labeled `.NET` + AG-UI (`ui integration`). The 10 **Not-AGUI** issues (pure DevUI/Aspire/MCP-UI
tooling) are intentionally not documented here. The remaining **43** each have a `<number>.md` doc.

Buckets and the action taken per the analysis:

- **GAP** — unknown-if-fixed: a unit test was added to validate. Confirmed-failing gaps are kept as
  `[Fact(Skip=...)]` (un-skip when fixed). Tests live in
  `tests/AGUI.{Abstractions,Client,Hosting.AspNetCore}.UnitTests/MafIssue*ValidationTests.cs`.
- **DEPRECATED** — the old MEAI `DataContent` smuggling is superseded by emitting the now-public
  AG-UI events directly; the doc carries a sample using the new approach.
- **FIXED** — already fixed/covered in our SDK; the doc cites the fixing code and a validating test
  (new tests in `MafIssueFixedTests_*.cs`, all passing).
- **AF-specific** — needs work on the Microsoft Agent Framework side; the doc proposes the steps for
  once MAF consumes our packages.

**Guiding principle:** if a concept is **not defined in the AG-UI protocol** but is achievable through
**extensibility**, the proposed approach is extensibility — not a default framework mapping. The
**preferred** path is a delegating `IChatClient`/`AIAgent` that emits a `CustomEvent`/`RawEvent` via
`ChatResponseUpdate.RawRepresentation` (see `Step06_RawEvents`); `AGUIStreamOptions.MapContent` is a
secondary seam aimed at framework integrators like MAF. (Applies to usage #3520/#3684/#3752 and annotations.)

Validation tests: full unit suite green — only #3962 (a genuine protocol-field bug) and #2699 are
skipped. Usage (#3520/#3684/#3752) and #3790 are by-design — captured as a snippet in their docs,
no test (per our rule). Everything else passes.
## GAP (11) — validation test added

| Issue | Title | Test result |
| --- | --- | --- |
| [#6519](6519.md) | Make AG-UI hosting (MapAGUI) transport-extensible | feature note (no test) |
| [#6479](6479.md) | Frontend tools break persisted chat history | AF-specific (history); our part: drop-or-throw dangling client-tool call, never synthesize |
| [#4869](4869.md) | AGUIChatClient sets ConversationId despite being stateless | PASS (characterization; design decision) |
| [#3962](3962.md) | messageId reused as toolCallId for TOOL_CALL_RESULT | FAIL → gap (skipped) |
| [#3790](3790.md) | Drops FinishReason on RunFinishedEvent | by-design (covered by interrupts; snippet in doc) |
| [#3752](3752.md) | Usage and Annotations not present | by-design; extensibility (snippet in doc) |
| [#3723](3723.md) | OpenTelemetry not emitted through AGUI | code-cited note (OTel; integration validation) |
| [#3684](3684.md) | UsageContent not returned in stream | by-design; extensibility (snippet in doc) |
| [#3520](3520.md) | Expose USAGE details to the front end | by-design; extensibility (snippet in doc) |
| [#3475](3475.md) | Inconsistent ag_ui_thread_id vs agui_thread_id | audit note (single-constant) |
| [#2699](2699.md) | Parallel tool calls produce invalid OpenAI history | FAIL → gap (skipped) |

## DEPRECATED (2) — use now-public AG-UI events (sample in doc)

| Issue | Title |
| --- | --- |
| [#4635](4635.md) | DataContent properties not propagated → emit CustomEvent/StateSnapshotEvent |
| [#2081](2081.md) | Intermediate state from tools → emit StateSnapshotEvent/StateDeltaEvent |

## FIXED (11) — already covered (code + test cited)

| Issue | Title | Test |
| --- | --- | --- |
| [#6511](6511.md) | Tool Call Error (non-JSON result) | PASS |
| [#5587](5587.md) | Crash on non-JSON TOOL_CALL_RESULT | PASS |
| [#5567](5567.md) | StateDeltaEvent deserialization | PASS |
| [#5209](5209.md) | Conversion API public | PASS |
| [#4342](4342.md) | ToolMessage MessageId preserved | PASS |
| [#3769](3769.md) | Transport decoupled | design-level |
| [#3729](3729.md) | Multimodal user messages | PASS |
| [#3365](3365.md) | ToolMessage MessageId not null | PASS |
| [#2637](2637.md) | parentMessageId null omitted | PASS |
| [#2558](2558.md) | More AG-UI event types | PASS |
| [#2510](2510.md) | MessagesSnapshot history sync | PASS |

## AF-specific (19) — proposal for once MAF consumes our packages

| Issue | Title |
| --- | --- |
| [#6006](6006.md) | DevUI unrecognized discriminator function_approval_response |
| [#5614](5614.md) | Convert Hosting.AGUI.AspNetCore to .NET Standard |
| [#4920](4920.md) | MapAGUI session store / AuthorName |
| [#4902](4902.md) | Which agent is executing (multi-agent) |
| [#4825](4825.md) | GitHubCopilotAgent permission.requested → HITL |
| [#4177](4177.md) | Auto-translate StateBag mutations to state events |
| [#3823](3823.md) | [Workflows] Session null in middleware |
| [#3215](3215.md) | Copilot Studio Agent integration |
| [#3033](3033.md) | FormatException on fractional created_at |
| [#3011](3011.md) | Handoffs workflow via DevUI |
| [#3002](3002.md) | Workflow as AG-UI agent client-side tools |
| [#2988](2988.md) | Dynamic agent resolution in endpoints |
| [#2959](2959.md) | Azure AI Projects per-request tools |
| [#2911](2911.md) | DevUI sub-workflow as WorkflowAsAgent |
| [#2702](2702.md) | ag-ui with OpenAI Responses API |
| [#2691](2691.md) | DevUI workflows complex Chat Protocol |
| [#2517](2517.md) | Thread persistence default in MapAGUI |
| [#2494](2494.md) | AG-UI support for workflow as agent |
| [#2179](2179.md) | Pass agent name (string) to MapAGUI |