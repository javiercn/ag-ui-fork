# Parallel tool calls — outbound result preservation + inbound shape

- Related issues: microsoft/agent-framework
  [#3962](https://github.com/microsoft/agent-framework/issues/3962) (tool-result identity) and
  [#2699](https://github.com/microsoft/agent-framework/issues/2699) (parallel-call history).
- Category: **Abstractions — bug fixed (outbound), inbound shape validated against a real LLM.**

## Summary
When a single assistant turn issues **two parallel backend tool calls** (e.g.
`get_weather(city)` + `get_current_time(timezone)`), MEAI/`FunctionInvokingChatClient`
collects the two results into **one** tool `ChatMessage` carrying **two**
`FunctionResultContent`s. The outbound conversion (`AsAGUIMessages`) dropped all but the
first result, so the AG-UI request history lost a tool result on every parallel turn.

## The bug (outbound `.NET → AG-UI`)
`AGUIChatMessageExtensions.AsAGUIMessages`, for a `ChatRole.Tool` message, did:

```csharp
var functionResult = message.Contents.OfType<FunctionResultContent>().FirstOrDefault();
// ... one AGUIToolMessage, ToolCallId = functionResult?.CallId, Id = message.MessageId
```

Two coupled defects:
1. `FirstOrDefault()` **drops every result after the first** when MEAI batches parallel
   results into one tool message.
2. `Id = message.MessageId` keys *all* emitted tool messages on the shared MEAI message id
   rather than on the (unique, deterministic) tool call id.

## The fix
`src/AGUI.Abstractions/Extensions/AGUIChatMessageExtensions.cs:192-217` — emit **one
`AGUIToolMessage` per `FunctionResultContent`**, each keyed on its own call id:

```csharp
foreach (var functionResult in functionResults)
{
    yield return new AGUIToolMessage
    {
        Id = functionResult.CallId,
        ToolCallId = functionResult.CallId,
        Content = SerializeFunctionResult(functionResult, message.Text)
    };
}
```

This matches the **response side**, which already sets
`TOOL_CALL_RESULT.messageId = toolCallId` (`src/AGUI.Hosting.AspNetCore/AGUIEventExtensions.cs:109`).
Tool messages are therefore keyed by `callId` in **both** directions — unique and
round-trippable (this is the by-design identity documented in `3962.md`). The
`SerializeFunctionResult` helper preserves the prior result-serialization behavior
(string / `JsonElement` / `IDictionary` / source-gen typeinfo / fallback text), AOT-safe.

## The inbound hypothesis — tested and **rejected**
**Hypothesis:** consecutive `AGUIToolMessage`s might need to be *collected* into a single
tool `ChatMessage` with multiple `FunctionResultContent`s (because some providers group
parallel results).

**Finding: the hypothesis does NOT hold.** AG-UI models each tool result as a separate
`ToolMessage` on the wire (TS `ToolMessageSchema`: required `id` + single `toolCallId`;
TS client creates one `ToolMessage` per `TOOL_CALL_RESULT`). Mapping each AG-UI tool
message **1:1** to a tool `ChatMessage` carrying one `FunctionResultContent` is exactly the
OpenAI-valid shape — the provider expects **one tool message per `tool_call_id`**. We
proved this empirically: the Step12 integration test and the cross-language Vitest test
both replay a **real recorded gpt-5-mini run** in which the C# server feeds the two
separate tool results back to the model and the model accepts them and produces the final
summary. No inbound grouping was needed, so `AsChatMessages` is left unchanged (1:1).

This is distinct from **#2699**, which is about parallel tool **calls** arriving inbound as
*separate assistant messages* that should coalesce into one assistant `ChatMessage`. That
remains an open gap with a `[Fact(Skip=...)]` validation test
(`AsChatMessages_ParallelToolCalls_CoalescedIntoSingleAssistantMessage`); the outbound fix
here does not touch it. See `2699.md`.

## Validation
- **Unit** (`tests/AGUI.Abstractions.UnitTests/AGUIChatMessageExtensionsTest.cs`):
  - `AsAGUIMessages_ToolMessageWithMultipleResults_EmitsOneMessagePerResult` — two results
    → two messages, distinct ids = call ids.
  - `AsChatMessages_MultipleToolMessages_MapToOneFunctionResultEach` — inbound 1:1 shape.
  - `AsAGUIMessages_ParallelToolResults_RoundTripPreservesBothResults` — full roundtrip
    preserves both call ids.
  - Updated `AsAGUIMessages_ToolMessage_PreservesToolCallId` — `Id` now equals the call id.
- **Integration** (`tests/AGUI.Hosting.AspNetCore.IntegrationTests/Samples/GettingStarted/Step12_ParallelToolCallsTest.cs`):
  layer-2 capture baselines + outbound/inbound roundtrip assertions over a recorded real
  Azure run (`fixtures/Step12_ParallelToolCalls/…recording.json`). Replays deterministically.
- **Cross-language** (`tests/CrossLanguage.Vitest/tests/parallel-tool-calls.test.ts` +
  `tests/CrossLanguage.TestServer/ParallelToolCallsRoute.cs` + AIMock fixture
  `fixtures/parallel-tool-calls.json`): TS `HttpAgent` → C# server resolves both parallel
  server tools and streams a summary mentioning both results.
- **Sample**: `samples/GettingStarted/Step12_ParallelToolCalls` (Server enables
  `FunctionInvokingChatClient.AllowConcurrentInvocation` so the two backend calls run
  concurrently).

## Decision
Outbound bug **fixed**. Inbound shape **kept 1:1** (validated against a real LLM). The
modified existing Mixed/Step03/Step04 request baselines are correct: they gain an `id`
field on tool messages equal to the `toolCallId` (re-derived deterministically from the
replay path), reflecting the new keying.
