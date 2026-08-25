---
name: agui-dotnet-human-in-the-loop
description: >
  Pause an AG-UI agent run for a human, then resume it, with the AG-UI .NET SDK — gate a sensitive tool behind explicit approval, or interrupt a run to collect free-form input from the user before continuing. USE FOR: requiring human approval before a tool executes (ApprovalRequiredAIFunction + ToolApprovalRequestContent/ToolApprovalResponseContent); pausing a function-backed workflow and resuming with FunctionCallContent / FunctionResultContent; how RUN_FINISHED outcome=interrupt and RunAgentInput.Resume carry suspension on the wire. DO NOT USE FOR: tools that run without approval on the server (use agui-dotnet-server-tools) or in the client (use agui-dotnet-client-tools); plain chat (use agui-dotnet-streaming-chat); shared state, generative UI, multimodal, or protobuf.
---

# AG-UI .NET — human in the loop (approval & interrupts)

Goal: stop an agent run at a decision point, hand control to a person, and resume the same run with their decision — either approving a sensitive tool call or supplying input the agent asked for.

A paused run completes its turn with `RUN_FINISHED { outcome: interrupt }`. The client inspects the response, gets the human decision, then sends a follow-up turn that carries the answer; the SDK reconnects it to the paused tool call so the run continues.

## Approval: gate a tool behind a human yes/no

This is the simple path and needs no custom endpoint code — `ToChatRequestContext` handles the resume.

### Server: wrap the sensitive tool

Wrap the function in `ApprovalRequiredAIFunction`. When the model calls it, the function-invoking client raises a `ToolApprovalRequestContent` instead of executing:

```csharp
using Microsoft.Extensions.AI;

var deleteFile = new ApprovalRequiredAIFunction(
    AIFunctionFactory.Create(DeleteFile, "delete_file", "Deletes a file."));

builder.Services.AddChatClient(/* provider IChatClient */)
    .ConfigureOptions(o => (o.Tools ??= []).Add(deleteFile))
    .UseFunctionInvocation();
```

### Client: detect the request, decide, resume

The first turn returns a `ToolApprovalRequestContent`. Show it to the human, then resume by appending the request and the human's response and streaming again:

```csharp
using Microsoft.Extensions.AI;

var messages = new List<ChatMessage> { new(ChatRole.User, "Delete report-draft.txt") };

var turn1 = new List<ChatResponseUpdate>();
await foreach (var u in client.GetStreamingResponseAsync(messages))
{
    turn1.Add(u);
}

var request = turn1.SelectMany(u => u.Contents)
                   .OfType<ToolApprovalRequestContent>()
                   .FirstOrDefault();

if (request is { ToolCall: FunctionCallContent call })
{
    bool approved = AskHuman($"Run {call.Name}?");   // your UI / prompt

    messages.Add(new ChatMessage(ChatRole.Assistant, [request]));
    messages.Add(new ChatMessage(ChatRole.User, [request.CreateResponse(approved)]));

    await foreach (var u in client.GetStreamingResponseAsync(messages))
    {
        Console.Write(u.Text);   // runs the tool if approved, skips it if not
    }
}
```

On the resumed turn the SDK re-pairs the approval request and response so the function-invoking client executes (or skips) the underlying call.

## Interrupt: ask the user for input mid-run

When a workflow needs free-form input, preserve its normal `FunctionCallContent`. Configure the endpoint's update-aware `MapInterrupt` overload to correlate an `AGUIInterrupt` with that call. `AGUIChatClient` returns the call as actionable and attaches the interruption as its raw representation:

```csharp
using AGUI.Abstractions;
using AGUI.Server;
using Microsoft.Extensions.AI;

var streamOptions = new AGUIStreamOptions().MapInterrupt(update =>
    update.Contents.OfType<FunctionCallContent>()
        .Where(call => call.Name == "request_user_input")
        .Select(call => new AGUIInterrupt
        {
            Id = call.CallId,
            ToolCallId = call.CallId,
            Reason = InterruptReasons.InputRequired,
            Message = "Enter a username"
        }));

var call = turn1.SelectMany(u => u.Contents)
                .OfType<FunctionCallContent>()
                .FirstOrDefault(c => c.RawRepresentation is AGUIInterrupt);

if (call is not null)
{
    var interrupt = (AGUIInterrupt)call.RawRepresentation!;
    var answer = AskHuman(interrupt.Message);

    messages.Add(new ChatMessage(ChatRole.Assistant, [call]));
    messages.Add(new ChatMessage(ChatRole.Tool,
        [new FunctionResultContent(call.CallId, answer)]));

    await foreach (var u in client.GetStreamingResponseAsync(messages))
    {
        Console.Write(u.Text);
    }
}
```

`AGUIChatClient` encodes the function result as `RunAgentInput.Resume[]` on the wire. Use `call.CreateCancellationResult()` instead when the human cancels. The interrupt carries a `Reason`, `Message`, and optional `ResponseSchema`.

On Resume, `ToChatRequestContext` validates the interruption's serialized call ID, name, and arguments against the latest unresolved function-call batch, then reconstructs the `FunctionResultContent` the workflow expects.

## Anti-patterns

- **Dropping the pending call.** Approval resumes need the `ToolApprovalRequestContent` and matching response; workflow resumes need the original `FunctionCallContent` and matching `FunctionResultContent` in the full message history.
- **Returning only some workflow results.** Every pending workflow interruption must receive exactly one function result before the client sends Resume.
- **Instructing the model to ask for confirmation in its prompt.** The approval gate is structural — the wrapped tool pauses the run on its own. A prompt that also tells the model to "ask the user to confirm" produces a redundant text question and a second round-trip. Tell the model to call the tool directly and let the gate handle approval.

## Verify

1. First turn ends without running the side effect: the stream finishes `RUN_FINISHED { outcome: interrupt }` and the response contains a `ToolApprovalRequestContent` or actionable workflow `FunctionCallContent`.
2. After resuming with approval, the tool's effect occurs and the run finishes normally; after resuming with a rejection, the effect does not occur and the model proceeds without it.
3. For input interrupts, the agent's final answer reflects the value the human supplied.
