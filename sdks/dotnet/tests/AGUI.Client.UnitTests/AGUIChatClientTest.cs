using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    // AGUIChatClient is a stateless client: it sends the full message history every turn.
    // It must NOT surface a ConversationId on returned updates, because MEAI agent wrappers
    // (e.g. AsAIAgent/ChatClientAgent) treat a returned ConversationId as a service-managed
    // session and then send only deltas on the next turn, truncating history against a
    // stateless AG-UI server. The AG-UI thread id is surfaced via AdditionalProperties instead.
    [Fact]
    public async Task GetStreamingResponse_DoesNotSurfaceConversationId()
    {
        var transport = new StaticTransport(
            new RunStartedEvent { ThreadId = "t1", RunId = "r1" },
            new TextMessageStartEvent { MessageId = "m1", Role = "assistant" },
            new TextMessageContentEvent { MessageId = "m1", Delta = "hi" },
            new TextMessageEndEvent { MessageId = "m1" },
            new RunFinishedEvent { ThreadId = "t1", RunId = "r1" });
        using var client = new AGUIChatClient(new() { Transport = transport });
        var options = new ChatOptions { ConversationId = "t1" };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }, options))
        {
            updates.Add(u);
        }

        Assert.All(updates, u => Assert.Null(u.ConversationId));
    }

    // https://github.com/microsoft/agent-framework/issues/4869
    // The AG-UI thread id is still observable on returned updates via AdditionalProperties,
    // even though it is never promoted to ConversationId. A caller-supplied ConversationId is
    // honored as the thread id.
    [Fact]
    public async Task GetStreamingResponse_SurfacesThreadIdInAdditionalProperties()
    {
        var transport = new StaticTransport(
            new RunStartedEvent { ThreadId = "t1", RunId = "r1" },
            new TextMessageStartEvent { MessageId = "m1", Role = "assistant" },
            new TextMessageContentEvent { MessageId = "m1", Delta = "hi" },
            new TextMessageEndEvent { MessageId = "m1" },
            new RunFinishedEvent { ThreadId = "t1", RunId = "r1" });
        using var client = new AGUIChatClient(new() { Transport = transport });
        var options = new ChatOptions { ConversationId = "t1" };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }, options))
        {
            updates.Add(u);
        }

        Assert.Contains(updates, u =>
            u.AdditionalProperties is not null
            && u.AdditionalProperties.TryGetValue("agui_thread_id", out string? threadId)
            && threadId == "t1");
    }

    // https://github.com/microsoft/agent-framework/issues/4869
    // When the caller reuses the same ChatOptions across turns and does not supply a
    // ConversationId, the client pins the generated AG-UI thread id onto the options so the
    // thread stays stable across turns — without ever advertising a ConversationId.
    [Fact]
    public async Task GetStreamingResponse_ReusedOptions_KeepsStableThreadIdWithoutConversationId()
    {
        var transport = new CapturingTransport(
            new TextMessageStartEvent { MessageId = "m1", Role = "assistant" },
            new TextMessageContentEvent { MessageId = "m1", Delta = "hi" },
            new TextMessageEndEvent { MessageId = "m1" });
        using var client = new AGUIChatClient(new() { Transport = transport });

        // Caller reuses the same ChatOptions instance across turns and supplies no ConversationId.
        var options = new ChatOptions();

        await DrainAsync(client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "turn one") }, options));

        var firstThreadId = transport.LastInput!.ThreadId;
        Assert.False(string.IsNullOrEmpty(firstThreadId));

        await DrainAsync(client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "turn two") }, options));

        // Same thread id is reused because it was pinned onto the reused options.
        Assert.Equal(firstThreadId, transport.LastInput!.ThreadId);
        Assert.Null(options.ConversationId);
        Assert.Equal(firstThreadId, options.AdditionalProperties?["agui_thread_id"]);
    }

    // https://github.com/microsoft/agent-framework/issues/4869
    // A fresh ChatOptions on each turn (no continuity hints) yields a different thread id per
    // turn — correctness is preserved because the full message history is sent every turn.
    [Fact]
    public async Task GetStreamingResponse_FreshOptionsPerTurn_GeneratesNewThreadId()
    {
        var transport = new CapturingTransport();
        using var client = new AGUIChatClient(new() { Transport = transport });

        await DrainAsync(client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "turn one") }, new ChatOptions()));
        var firstThreadId = transport.LastInput!.ThreadId;

        await DrainAsync(client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "turn two") }, new ChatOptions()));
        var secondThreadId = transport.LastInput!.ThreadId;

        Assert.NotEqual(firstThreadId, secondThreadId);
    }

    // https://github.com/microsoft/agent-framework/issues/4869
    // The full message history is sent to the transport on every turn (stateless protocol),
    // regardless of thread continuity.
    [Fact]
    public async Task GetStreamingResponse_SendsFullHistoryEveryTurn()
    {
        var transport = new CapturingTransport();
        using var client = new AGUIChatClient(new() { Transport = transport });
        var options = new ChatOptions();

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "first"),
            new(ChatRole.Assistant, "reply"),
            new(ChatRole.User, "second"),
        };

        await DrainAsync(client.GetStreamingResponseAsync(history, options));

        Assert.Equal(3, transport.LastInput!.Messages.Count);
    }

    // https://github.com/ag-ui-protocol/ag-ui/issues/2151
    // A caller-supplied RunAgentInput (via RawRepresentationFactory) must forward
    // Context and ForwardedProperties onto the request actually sent, alongside
    // the already-forwarded Messages/Tools/State/ParentRunId.
    [Fact]
    public async Task GetStreamingResponse_RawRepresentationFactory_ForwardsContextAndForwardedProperties()
    {
        var transport = new CapturingTransport();
        using var client = new AGUIChatClient(new() { Transport = transport });

        var forwardedProperties = JsonDocument.Parse("{\"tenant\":\"acme\"}").RootElement.Clone();

        var options = new ChatOptions
        {
            RawRepresentationFactory = _ => new RunAgentInput
            {
                Context = new List<AGUIContext>
                {
                    new() { Description = "userId", Value = "u-123" },
                },
                ForwardedProperties = forwardedProperties,
            },
        };

        var history = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        await DrainAsync(client.GetStreamingResponseAsync(history, options));

        var sent = transport.LastInput!;

        Assert.NotNull(sent.Context);
        var context = Assert.Single(sent.Context!);
        Assert.Equal("userId", context.Description);
        Assert.Equal("u-123", context.Value);

        Assert.Equal(JsonValueKind.Object, sent.ForwardedProperties.ValueKind);
        Assert.Equal("acme", sent.ForwardedProperties.GetProperty("tenant").GetString());
    }

    // A caller-supplied Resume (via RawRepresentationFactory) must be forwarded too,
    // like the other RunAgentInput fields (#2177 review follow-up).
    [Fact]
    public async Task GetStreamingResponse_RawRepresentationFactory_ForwardsResume()
    {
        var transport = new CapturingTransport();
        using var client = new AGUIChatClient(new() { Transport = transport });

        var options = new ChatOptions
        {
            RawRepresentationFactory = _ => new RunAgentInput
            {
                Resume = new List<AGUIResume>
                {
                    new() { InterruptId = "caller-interrupt", Status = ResumeStatus.Resolved },
                },
            },
        };

        var history = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        await DrainAsync(client.GetStreamingResponseAsync(history, options));

        var resume = transport.LastInput!.Resume;
        Assert.NotNull(resume);
        var entry = Assert.Single(resume!);
        Assert.Equal("caller-interrupt", entry.InterruptId);
    }

    // A caller-supplied Resume takes precedence over the approval-response
    // translation (the callerSuppliedResume guard yields to it) (#2177).
    [Fact]
    public async Task GetStreamingResponse_CallerResume_TakesPrecedenceOverApprovalResponses()
    {
        var transport = new CapturingTransport();
        using var client = new AGUIChatClient(new() { Transport = transport });

        var options = new ChatOptions
        {
            RawRepresentationFactory = _ => new RunAgentInput
            {
                Resume = new List<AGUIResume>
                {
                    new() { InterruptId = "caller-interrupt", Status = ResumeStatus.Resolved },
                },
            },
        };

        var toolCall = new FunctionCallContent("call-1", "someTool", new Dictionary<string, object?>());
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, [new ToolApprovalResponseContent("req-approval", approved: true, toolCall)]),
        };

        await DrainAsync(client.GetStreamingResponseAsync(history, options));

        // The caller's Resume wins; the approval response is not translated over it.
        var resume = transport.LastInput!.Resume;
        Assert.NotNull(resume);
        var entry = Assert.Single(resume!);
        Assert.Equal("caller-interrupt", entry.InterruptId);
    }

    [Fact]
    public async Task GetStreamingResponse_ApprovalFilteringPreservesPeerToolCalls()
    {
        var transport = new CapturingTransport();
        using var client = new AGUIChatClient(new() { Transport = transport });
        var peerCall = new FunctionCallContent("peer-call", "server_tool");
        var approvalCall = new FunctionCallContent("approval-call", "protected_tool");
        var approval = new ToolApprovalRequestContent("approval-request", approvalCall);
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [peerCall, approval]),
            new(ChatRole.User, [approval.CreateResponse(approved: true)]),
        };

        await DrainAsync(client.GetStreamingResponseAsync(history));

        var assistant = Assert.Single(transport.LastInput!.Messages.OfType<AGUIAssistantMessage>());
        Assert.Collection(
            assistant.ToolCalls!,
            toolCall =>
            {
                Assert.Equal("peer-call", toolCall.Id);
                Assert.Equal("server_tool", toolCall.Function.Name);
            },
            toolCall =>
            {
                Assert.Equal("approval-call", toolCall.Id);
                Assert.Equal("protected_tool", toolCall.Function.Name);
            });
        Assert.Equal("approval-request", Assert.Single(transport.LastInput.Resume!).InterruptId);
    }

    [Fact]
    public async Task GetStreamingResponse_MissingPeerApprovalResponseThrows()
    {
        var transport = new CapturingTransport();
        using var client = new AGUIChatClient(new() { Transport = transport });
        var first = new ToolApprovalRequestContent(
            "approval-1",
            new FunctionCallContent("call-1", "first_tool"));
        var second = new ToolApprovalRequestContent(
            "approval-2",
            new FunctionCallContent("call-2", "second_tool"));
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [first, second]),
            new(ChatRole.Tool, [new FunctionResultContent("call-2", "already-complete")]),
            new(ChatRole.User, [first.CreateResponse(approved: true)]),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DrainAsync(client.GetStreamingResponseAsync(history))).ConfigureAwait(true);

        Assert.Contains("approval-2", exception.Message);
        Assert.Null(transport.LastInput);
    }

    [Fact]
    public async Task GetStreamingResponse_CrossedApprovalResponsesThrow()
    {
        var transport = new CapturingTransport();
        using var client = new AGUIChatClient(new() { Transport = transport });
        var first = new ToolApprovalRequestContent(
            "approval-1",
            new FunctionCallContent("call-1", "first_tool"));
        var second = new ToolApprovalRequestContent(
            "approval-2",
            new FunctionCallContent("call-2", "second_tool"));
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [first, second]),
            new(ChatRole.User,
            [
                new ToolApprovalResponseContent("approval-1", true, second.ToolCall),
                new ToolApprovalResponseContent("approval-2", false, first.ToolCall),
            ]),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DrainAsync(client.GetStreamingResponseAsync(history))).ConfigureAwait(true);

        Assert.Contains("does not match its original request", exception.Message);
        Assert.Null(transport.LastInput);
    }

    [Fact]
    public async Task GetStreamingResponse_RoutesClientApprovalLocallyAndServerApprovalToResume()
    {
        var transport = new CapturingTransport();
        using var client = new AGUIChatClient(new() { Transport = transport });
        var clientInvocations = 0;
        var clientTool = AIFunctionFactory.Create(
            () =>
            {
                clientInvocations++;
                return "client-result";
            },
            "client_tool");
        var clientRequest = new ToolApprovalRequestContent(
            "client-approval",
            new FunctionCallContent("client-call", "client_tool"))
        {
#pragma warning disable MEAI001
            RequiresConfirmation = false,
#pragma warning restore MEAI001
        };
        var serverRequest = new ToolApprovalRequestContent(
            "server-approval",
            new FunctionCallContent("server-call", "protected_server_tool"));
        var peerRequest = new ToolApprovalRequestContent(
            "peer-approval",
            new FunctionCallContent("peer-call", "normal_server_tool")
            {
                InformationalOnly = true,
            })
        {
#pragma warning disable MEAI001
            RequiresConfirmation = false,
#pragma warning restore MEAI001
        };
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [clientRequest, serverRequest, peerRequest]),
            new(ChatRole.User,
            [
                clientRequest.CreateResponse(approved: true),
                serverRequest.CreateResponse(approved: true),
                peerRequest.CreateResponse(approved: true),
            ]),
        };

        await DrainAsync(client.GetStreamingResponseAsync(
            history,
            new ChatOptions { Tools = [clientTool] }));

        Assert.Equal(1, clientInvocations);
        var resume = Assert.Single(transport.LastInput!.Resume!);
        Assert.Equal("server-approval", resume.InterruptId);
        Assert.Equal(
            ["client-call", "server-call", "peer-call"],
            transport.LastInput.Messages
                .OfType<AGUIAssistantMessage>()
                .SelectMany(message => message.ToolCalls ?? [])
                .Select(call => call.Id));
        Assert.Contains(
            transport.LastInput.Messages.OfType<AGUIToolMessage>(),
            message => message.ToolCallId == "client-call");
    }

    [Fact]
    public async Task GetStreamingResponse_CombinesFunctionInterruptAndServerApprovalResumes()
    {
        var transport = new CapturingTransport();
        using var client = new AGUIChatClient(new() { Transport = transport });
        var interrupt = new AGUIInterrupt
        {
            Id = "interrupt-call",
            ToolCallId = "interrupt-call",
            Reason = InterruptReasons.InputRequired,
        };
        var interruptedCall = new FunctionCallContent("interrupt-call", "collect_input")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [AGUIClientInternalKeys.Interrupt] = JsonSerializer.SerializeToElement(
                    interrupt,
                    AGUIJsonSerializerContext.Default.AGUIInterrupt),
                [AGUIClientInternalKeys.InterruptThreadId] = "thread-interrupt",
            },
            RawRepresentation = interrupt,
        };
        var serverRequest = new ToolApprovalRequestContent(
            "server-approval",
            new FunctionCallContent("server-call", "protected_server_tool"));
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [interruptedCall, serverRequest]),
            new(ChatRole.Tool, [new FunctionResultContent(interruptedCall.CallId, "answer")]),
            new(ChatRole.User, [serverRequest.CreateResponse(approved: true)]),
        };

        await DrainAsync(client.GetStreamingResponseAsync(history));

        Assert.Equal(
            ["server-approval", "interrupt-call"],
            transport.LastInput!.Resume!.Select(resume => resume.InterruptId));
        Assert.DoesNotContain(
            transport.LastInput.Messages.OfType<AGUIToolMessage>(),
            message => message.ToolCallId == interruptedCall.CallId);
    }

    [Fact]
    public async Task GetStreamingResponse_CompletedMixedTurnDoesNotCreateResume()
    {
        var transport = new CapturingTransport();
        using var client = new AGUIChatClient(new() { Transport = transport });
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "client-result", "client_tool")],
        };
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "first turn"),
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("client-call", "client_tool"),
                new FunctionCallContent("server-call", "server_tool"),
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("client-call", "client-result")]),
            new(ChatRole.Assistant, "finished"),
            new(ChatRole.User, "new turn"),
        };

        await DrainAsync(client.GetStreamingResponseAsync(history, options));

        Assert.Null(transport.LastInput!.Resume);
    }

    [Fact]
    public async Task GetStreamingResponse_CompletedServerPeerDoesNotCreateResume()
    {
        var transport = new CapturingTransport();
        using var client = new AGUIChatClient(new() { Transport = transport });
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "unused", "client_tool")],
        };
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("client-call", "client_tool"),
                new FunctionCallContent("server-call", "server_tool"),
            ]),
            new(ChatRole.Tool,
            [
                new FunctionResultContent("client-call", "client-result"),
                new FunctionResultContent("server-call", "server-result"),
            ]),
        };

        await DrainAsync(client.GetStreamingResponseAsync(history, options));

        Assert.Null(transport.LastInput!.Resume);
    }

    [Fact]
    public async Task GetStreamingResponse_FunctionInterruptSurfacesActionableFunctionCallAndResumesWithResult()
    {
        var interruptTurn = CreateInterruptTurn(("call-w", "collect_input"));
        var emittedInterrupt = Assert.Single(
            Assert.IsType<RunFinishedInterruptOutcome>(
                Assert.IsType<RunFinishedEvent>(interruptTurn[^1]).Outcome).Interrupts);
        emittedInterrupt.Metadata = JsonDocument.Parse("""
            {
              "custom": "preserved",
              "ag-ui": {
                "existing": true
              }
            }
            """).RootElement.Clone();
        var transport = new CapturingTransport(interruptTurn);
        using var client = new AGUIChatClient(new() { Transport = transport });
        var options = new ChatOptions { ConversationId = "thread-interrupt" };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "start")],
            options))
        {
            updates.Add(update);
        }

        var call = Assert.Single(updates.SelectMany(update => update.Contents)
            .OfType<FunctionCallContent>());
        Assert.False(call.InformationalOnly);
        var interrupt = Assert.IsType<AGUIInterrupt>(call.RawRepresentation);
        Assert.Equal("call-w", interrupt.Id);

        await DrainAsync(client.GetStreamingResponseAsync(
        [
            new ChatMessage(ChatRole.User, "start"),
            new ChatMessage(ChatRole.Assistant, [call]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, "answer")]),
        ],
        options));

        var resume = Assert.Single(transport.LastInput!.Resume!);
        Assert.Equal("call-w", resume.InterruptId);
        Assert.Equal(ResumeStatus.Resolved, resume.Status);
        Assert.Equal("answer", resume.Payload!.Value.GetString());
        Assert.Equal("preserved", resume.Metadata!.Value.GetProperty("custom").GetString());
        var aguiMetadata = resume.Metadata.Value.GetProperty(AGUIMetadata.ReservedKey);
        Assert.True(aguiMetadata.GetProperty("existing").GetBoolean());
        Assert.DoesNotContain(
            transport.LastInput.Messages,
            message => message is AGUIToolMessage tool && tool.ToolCallId == call.CallId);
    }

    [Fact]
    public async Task GetStreamingResponse_ThreeInterruptResponsesMayBeOutOfOrderButMustBeComplete()
    {
        var transport = new CapturingTransport(CreateInterruptTurn(
            ("call-w1", "collect_one"),
            ("call-w2", "collect_two"),
            ("call-w3", "collect_three")));
        using var client = new AGUIChatClient(new() { Transport = transport });
        var options = new ChatOptions { ConversationId = "thread-interrupt" };
        var firstTurn = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "start")],
            options))
        {
            firstTurn.Add(update);
        }

        var calls = firstTurn.SelectMany(update => update.Contents)
            .OfType<FunctionCallContent>()
            .ToDictionary(call => call.CallId, StringComparer.Ordinal);
        await DrainAsync(client.GetStreamingResponseAsync(
        [
            new ChatMessage(ChatRole.Assistant, calls.Values.Cast<AIContent>().ToList()),
            new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent("call-w3", 3),
                new FunctionResultContent("call-w1", 1),
                new FunctionResultContent("call-w2", 2),
            ]),
        ],
        options));

        Assert.Equal(
            ["call-w1", "call-w2", "call-w3"],
            transport.LastInput!.Resume!.Select(resume => resume.InterruptId));
    }

    [Fact]
    public async Task GetStreamingResponse_MissingInterruptResponseIsRejectedBeforeAnotherRequest()
    {
        var transport = new CapturingTransport(CreateInterruptTurn(
            ("call-w1", "collect_one"),
            ("call-w2", "collect_two")));
        using var client = new AGUIChatClient(new() { Transport = transport });
        var options = new ChatOptions { ConversationId = "thread-interrupt" };
        var firstTurn = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "start")],
            options))
        {
            firstTurn.Add(update);
        }
        var calls = firstTurn.SelectMany(update => update.Contents)
            .OfType<FunctionCallContent>()
            .ToList();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(
            client.GetStreamingResponseAsync(
            [
                new ChatMessage(ChatRole.Assistant, calls.Cast<AIContent>().ToList()),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent(calls[0].CallId, "only one")]),
            ],
            options)));

        Assert.Contains("Interrupt responses are missing", exception.Message);
        Assert.Equal(1, transport.SendCount);
    }

    [Fact]
    public async Task GetStreamingResponse_OperationCanceledExceptionProducesCancelledResume()
    {
        var transport = new CapturingTransport(CreateInterruptTurn(
            ("call-w", "collect_input")));
        using var client = new AGUIChatClient(new() { Transport = transport });
        var options = new ChatOptions { ConversationId = "thread-interrupt" };
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "start")],
            options))
        {
            updates.Add(update);
        }
        var call = Assert.Single(updates.SelectMany(update => update.Contents)
            .OfType<FunctionCallContent>());

        var cancellation = new OperationCanceledException("cancelled by user");
        await DrainAsync(client.GetStreamingResponseAsync(
        [
            new ChatMessage(ChatRole.Assistant, [call]),
            new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent(call.CallId, "must not be sent")
                {
                    Exception = cancellation,
                },
            ]),
        ],
        options));

        var resume = Assert.Single(transport.LastInput!.Resume!);
        Assert.Equal(ResumeStatus.Cancelled, resume.Status);
        Assert.Null(resume.Payload);
        Assert.Equal("cancelled by user", resume.Metadata!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task GetStreamingResponse_NonCancellationInterruptExceptionIsRejectedBeforeAnotherRequest()
    {
        var transport = new CapturingTransport(CreateInterruptTurn(
            ("call-w", "collect_input")));
        using var client = new AGUIChatClient(new() { Transport = transport });
        var options = new ChatOptions { ConversationId = "thread-interrupt" };
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "start")],
            options))
        {
            updates.Add(update);
        }
        var call = Assert.Single(updates.SelectMany(update => update.Contents)
            .OfType<FunctionCallContent>());
        var failure = new InvalidOperationException("input failed");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(
            client.GetStreamingResponseAsync(
            [
                new ChatMessage(ChatRole.Assistant, [call]),
                new ChatMessage(ChatRole.Tool,
                [
                    new FunctionResultContent(call.CallId, result: null)
                    {
                        Exception = failure,
                    },
                ]),
            ],
            options)));

        Assert.Equal(
            "Interrupted function failed instead of being resolved or cancelled.",
            exception.Message);
        Assert.Same(failure, exception.InnerException);
        Assert.Equal(1, transport.SendCount);
    }

    [Fact]
    public async Task GetStreamingResponse_CompletedInterruptInHistoryDoesNotResumeOnLaterOrdinaryTurn()
    {
        var transport = new CapturingTransport(CreateInterruptTurn(("call-w", "collect_input")));
        using var client = new AGUIChatClient(new() { Transport = transport });
        var options = new ChatOptions { ConversationId = "thread-interrupt" };
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "start")],
            options))
        {
            updates.Add(update);
        }
        var call = Assert.Single(updates.SelectMany(update => update.Contents)
            .OfType<FunctionCallContent>());
        var result = new FunctionResultContent(call.CallId, "answer");

        await DrainAsync(client.GetStreamingResponseAsync(
        [
            new ChatMessage(ChatRole.Assistant, [call]),
            new ChatMessage(ChatRole.Tool, [result]),
            new ChatMessage(ChatRole.User, "next question"),
        ],
        options));

        Assert.Null(transport.LastInput!.Resume);
    }

    [Fact]
    public async Task GetStreamingResponse_LocalToolFailureWithPendingInterruptDoesNotSendAnotherRequest()
    {
        var transport = new CapturingTransport(CreateMixedInterruptTurn());
        using var client = new AGUIChatClient(new() { Transport = transport });
        var firstInvocations = 0;
        var secondInvocations = 0;
        var failure = new InvalidOperationException("client tool failed");
        var options = new ChatOptions
        {
            ConversationId = "thread-interrupt",
            Tools =
            [
                AIFunctionFactory.Create(() =>
                {
                    firstInvocations++;
                    return "first";
                }, "client_one"),
                AIFunctionFactory.Create(() =>
                {
                    secondInvocations++;
                    throw failure;
                }, "client_two"),
            ],
        };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "start")],
            options))
        {
            updates.Add(update);
        }

        Assert.Equal(1, transport.SendCount);
        Assert.Equal(1, firstInvocations);
        Assert.Equal(1, secondInvocations);
        var failedResult = Assert.Single(
            updates.SelectMany(update => update.Contents).OfType<FunctionResultContent>(),
            result => result.CallId == "call-c2");
        Assert.Same(failure, failedResult.Exception);
    }

    [Fact]
    public async Task GetStreamingResponse_TwoClientToolsAndInterruptedCallExecuteClientsExactlyOnceWithoutPrematureRequest()
    {
        var transport = new CapturingTransport(CreateMixedInterruptTurn());
        using var client = new AGUIChatClient(new() { Transport = transport });
        var firstInvocations = 0;
        var secondInvocations = 0;
        var options = new ChatOptions
        {
            ConversationId = "thread-interrupt",
            Tools =
            [
                AIFunctionFactory.Create(() =>
                {
                    firstInvocations++;
                    return "first";
                }, "client_one"),
                AIFunctionFactory.Create(() =>
                {
                    secondInvocations++;
                    return "second";
                }, "client_two"),
            ],
        };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "start")],
            options))
        {
            updates.Add(update);
        }

        Assert.Equal(1, transport.SendCount);
        Assert.Equal(1, firstInvocations);
        Assert.Equal(1, secondInvocations);
        var interruptedCall = updates.SelectMany(update => update.Contents)
            .OfType<FunctionCallContent>()
            .Single(call => call.Name == "collect_input");
        Assert.False(interruptedCall.InformationalOnly);
    }

    // https://github.com/microsoft/agent-framework/issues/5587
    [Fact]
    public async Task AGUIChatClient_ToolCallResultWithPlainTextContent_DoesNotParseAsJson()
    {
        var client = new AGUIChatClient(new() { Transport = new StaticTransport(
            new RunStartedEvent { ThreadId = "thread-1", RunId = "run-1" },
            new ToolCallResultEvent
            {
                MessageId = "msg-1",
                ToolCallId = "call-1",
                Content = "Transferred.",
                Role = AGUIRoles.Tool
            },
            new RunFinishedEvent { ThreadId = "thread-1", RunId = "run-1" }) });

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
    public async Task AGUIChatClient_InterruptedToolCallResultWithPlainTextContent_DoesNotThrowJsonException()
    {
        var client = new AGUIChatClient(new() { Transport = new StaticTransport(
            new RunStartedEvent { ThreadId = "thread-1", RunId = "run-1" },
            new ToolCallResultEvent
            {
                MessageId = "msg-1",
                ToolCallId = "call-1",
                Content = "Expense report ER-1 approved",
                Role = AGUIRoles.Tool
            },
            new RunFinishedEvent { ThreadId = "thread-1", RunId = "run-1" }) });

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

    private static async Task DrainAsync(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        await foreach (var _ in updates.ConfigureAwait(false))
        {
        }
    }

    private static BaseEvent[] CreateInterruptTurn(
        params (string CallId, string Name)[] calls)
    {
        var events = new List<BaseEvent>();
        foreach (var call in calls)
        {
            events.Add(new ToolCallStartEvent
            {
                ToolCallId = call.CallId,
                ToolCallName = call.Name,
            });
            events.Add(new ToolCallArgsEvent { ToolCallId = call.CallId, Delta = "{}" });
            events.Add(new ToolCallEndEvent { ToolCallId = call.CallId });
        }
        events.Add(new RunFinishedEvent
        {
            ThreadId = "thread-interrupt",
            RunId = "run-interrupt",
            Outcome = new RunFinishedInterruptOutcome
            {
                Interrupts = calls.Select(call => new AGUIInterrupt
                {
                    Id = call.CallId,
                    ToolCallId = call.CallId,
                    Reason = InterruptReasons.InputRequired,
                }).ToList(),
            },
        });
        return events.ToArray();
    }

    private static BaseEvent[] CreateMixedInterruptTurn()
    {
        var events = new List<BaseEvent>();
        foreach (var call in new[]
        {
            (CallId: "call-c1", Name: "client_one"),
            (CallId: "call-c2", Name: "client_two"),
            (CallId: "call-w", Name: "collect_input"),
        })
        {
            events.Add(new ToolCallStartEvent
            {
                ToolCallId = call.CallId,
                ToolCallName = call.Name,
            });
            events.Add(new ToolCallArgsEvent { ToolCallId = call.CallId, Delta = "{}" });
            events.Add(new ToolCallEndEvent { ToolCallId = call.CallId });
        }
        events.Add(new RunFinishedEvent
        {
            ThreadId = "thread-interrupt",
            RunId = "run-interrupt",
            Outcome = new RunFinishedInterruptOutcome
            {
                Interrupts =
                [
                    new AGUIInterrupt
                    {
                        Id = "call-w",
                        ToolCallId = "call-w",
                        Reason = InterruptReasons.InputRequired,
                    },
                ],
            },
        });
        return events.ToArray();
    }

    [Fact]
    public async Task ClientToolExecution_EmitsExecuteToolSpan_OnAGUIClientSource()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AGUIClientInstrumentation.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (activities)
                {
                    activities.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        // Turn 1 surfaces a call to the client tool; turn 2 (after the client executes it) finishes.
        var transport = new SequencedTransport(
            new BaseEvent[]
            {
                new ToolCallStartEvent { ToolCallId = "call-1", ToolCallName = "probe_location" },
                new ToolCallArgsEvent { ToolCallId = "call-1", Delta = "{}" },
                new ToolCallEndEvent { ToolCallId = "call-1" },
            },
            System.Array.Empty<BaseEvent>());

        var client = new AGUIChatClient(new AGUIChatClientOptions { Transport = transport });
        var tool = AIFunctionFactory.Create(() => "Amsterdam, NL", "probe_location", "Gets the user's location.");
        var options = new ChatOptions { Tools = [tool] };

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Where am I?")], options).ConfigureAwait(false))
        {
        }

        List<Activity> snapshot;
        lock (activities)
        {
            snapshot = activities.ToList();
        }

        Assert.Contains(snapshot, a =>
            a.DisplayName == "execute_tool probe_location"
            && (string?)a.GetTagItem("gen_ai.tool.name") == "probe_location");
    }

    private sealed class SequencedTransport(params BaseEvent[][] turns) : IAGUITransport
    {
        private int _call;

        public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var index = System.Math.Min(_call, turns.Length - 1);
            _call++;

            yield return new RunStartedEvent { ThreadId = input.ThreadId, RunId = input.RunId };

            foreach (var evt in turns[index])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return evt;
            }

            yield return new RunFinishedEvent { ThreadId = input.ThreadId, RunId = input.RunId };

            await Task.CompletedTask.ConfigureAwait(false);
        }
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

    [Fact]
    public async Task GetStreamingResponse_SurfacesRunFinishedUsageAsUsageContent()
    {
        var transport = new StaticTransport(
            new RunStartedEvent { ThreadId = "t1", RunId = "r1" },
            new TextMessageStartEvent { MessageId = "m1", Role = "assistant" },
            new TextMessageContentEvent { MessageId = "m1", Delta = "hi" },
            new TextMessageEndEvent { MessageId = "m1" },
            new RunFinishedEvent
            {
                ThreadId = "t1",
                RunId = "r1",
                Usage =
                [
                    new TokenUsage
                    {
                        Provider = "openai",
                        Model = "gpt-4o",
                        InputTokens = 11,
                        OutputTokens = 22,
                        TotalTokens = 33,
                        ReasoningTokens = 44,
                        CachedInputTokens = 55
                    }
                ]
            });
        using var client = new AGUIChatClient(new() { Transport = transport });

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }))
        {
            updates.Add(u);
        }

        // ToChatResponse aggregates UsageContent across updates — this is how a caller of
        // the IChatClient abstraction actually reads usage.
        var usage = updates.ToChatResponse().Usage;
        Assert.NotNull(usage);
        Assert.Equal(11, usage.InputTokenCount);
        Assert.Equal(22, usage.OutputTokenCount);
        Assert.Equal(33, usage.TotalTokenCount);
        Assert.Equal(44, usage.ReasoningTokenCount);
        Assert.Equal(55, usage.CachedInputTokenCount);
    }

    [Fact]
    public async Task GetStreamingResponse_UsageContentCarriesModelId()
    {
        var transport = new StaticTransport(
            new RunStartedEvent { ThreadId = "t1", RunId = "r1" },
            new RunFinishedEvent
            {
                ThreadId = "t1",
                RunId = "r1",
                Usage =
                [
                    new TokenUsage { Model = "gpt-4o", InputTokens = 10 },
                    new TokenUsage { Model = "gpt-4o-mini", InputTokens = 5 }
                ]
            });
        using var client = new AGUIChatClient(new() { Transport = transport });

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }))
        {
            updates.Add(u);
        }

        // Per-model attribution must survive: one UsageContent per entry, each labelled.
        var usageUpdates = updates
            .Where(u => u.Contents.OfType<UsageContent>().Any())
            .ToList();

        Assert.Equal(2, usageUpdates.Count);
        Assert.Equal("gpt-4o", usageUpdates[0].ModelId);
        Assert.Equal(10, usageUpdates[0].Contents.OfType<UsageContent>().Single().Details.InputTokenCount);
        Assert.Equal("gpt-4o-mini", usageUpdates[1].ModelId);
        Assert.Equal(5, usageUpdates[1].Contents.OfType<UsageContent>().Single().Details.InputTokenCount);
    }

    [Fact]
    public async Task GetStreamingResponse_NoUsage_EmitsNoUsageContent()
    {
        var transport = new StaticTransport(
            new RunStartedEvent { ThreadId = "t1", RunId = "r1" },
            new RunFinishedEvent { ThreadId = "t1", RunId = "r1" });
        using var client = new AGUIChatClient(new() { Transport = transport });

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }))
        {
            updates.Add(u);
        }

        Assert.DoesNotContain(updates, u => u.Contents.OfType<UsageContent>().Any());
        Assert.Null(updates.ToChatResponse().Usage);
    }

    private sealed class CapturingTransport(params BaseEvent[] middleEvents) : IAGUITransport
    {
        public RunAgentInput? LastInput { get; private set; }

        public int SendCount { get; private set; }

        public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastInput = input;
            SendCount++;

            // Echo the thread/run ids back like a real stateless AG-UI server.
            yield return new RunStartedEvent { ThreadId = input.ThreadId, RunId = input.RunId };

            foreach (var evt in middleEvents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return evt;
            }

            if (!middleEvents.Any(evt => evt is RunFinishedEvent))
            {
                yield return new RunFinishedEvent { ThreadId = input.ThreadId, RunId = input.RunId };
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
