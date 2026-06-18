using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AGUI.Abstractions;
using AGUI.Client;
using Microsoft.Extensions.AI;
using Xunit;

namespace AGUI.Client.UnitTests;

public sealed class AGUIChatClientTest
{
    // https://github.com/microsoft/agent-framework/issues/4869
    // Issue #4869 — characterization test. AGUIChatClient echoes the caller-supplied
    // ConversationId back on returned updates. The issue argues this misleads AsAIAgent
    // wrappers (they then send only deltas). Whether to stop echoing is an OPEN DESIGN
    // DECISION, so this test documents the CURRENT behavior rather than asserting a fix.
    [Fact]
    public async Task GetStreamingResponse_EchoesCallerConversationId()
    {
        var transport = new StaticTransport(
            new RunStartedEvent { ThreadId = "t1", RunId = "r1" },
            new TextMessageStartEvent { MessageId = "m1", Role = "assistant" },
            new TextMessageContentEvent { MessageId = "m1", Delta = "hi" },
            new TextMessageEndEvent { MessageId = "m1" },
            new RunFinishedEvent { ThreadId = "t1", RunId = "r1" });
        using var client = new AGUIChatClient(transport);
        var options = new ChatOptions { ConversationId = "t1" };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }, options))
        {
            updates.Add(u);
        }

        Assert.Contains(updates, u => u.ConversationId == "t1");
    }

    // https://github.com/microsoft/agent-framework/issues/5587
    [Fact]
    public async Task AGUIChatClient_ToolCallResultWithPlainTextContent_DoesNotParseAsJson()
    {
        var client = new AGUIChatClient(new StaticTransport(
            new RunStartedEvent { ThreadId = "thread-1", RunId = "run-1" },
            new ToolCallResultEvent
            {
                MessageId = "msg-1",
                ToolCallId = "call-1",
                Content = "Transferred.",
                Role = AGUIRoles.Tool
            },
            new RunFinishedEvent { ThreadId = "thread-1", RunId = "run-1" }));

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "start")],
            cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            updates.Add(update);
        }

        var result = Assert.Single(updates.SelectMany(static update => update.Contents).OfType<FunctionResultContent>());
        Assert.Equal("call-1", result.CallId);
        Assert.Equal("Transferred.", result.Result);
    }

    // https://github.com/microsoft/agent-framework/issues/6511
    [Fact]
    public async Task AGUIChatClient_WorkflowToolCallResultWithPlainTextContent_DoesNotThrowJsonException()
    {
        var client = new AGUIChatClient(new StaticTransport(
            new RunStartedEvent { ThreadId = "thread-1", RunId = "run-1" },
            new ToolCallResultEvent
            {
                MessageId = "msg-1",
                ToolCallId = "call-1",
                Content = "Expense report ER-1 approved",
                Role = AGUIRoles.Tool
            },
            new RunFinishedEvent { ThreadId = "thread-1", RunId = "run-1" }));

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "approve ER-1")],
            cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            updates.Add(update);
        }

        var result = Assert.Single(updates.SelectMany(static update => update.Contents).OfType<FunctionResultContent>());
        Assert.Equal("Expense report ER-1 approved", result.Result);
    }

    private sealed class StaticTransport(params BaseEvent[] events) : IAGUITransport
    {
        public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var evt in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return evt;
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
