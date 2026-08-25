using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace AGUI.Client;

/// <summary>
/// Provides an <see cref="IChatClient"/> implementation for AG-UI protocol.
/// </summary>
public sealed class AGUIChatClient : DelegatingChatClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AGUIChatClient"/> class.
    /// </summary>
    /// <param name="options">The options that configure the transport and serialization.</param>
    public AGUIChatClient(AGUIChatClientOptions options)
        : base(CreateInnerClient(GetTransport(options), CombineJsonSerializerOptions(options?.JsonSerializerOptions)))
    {
    }

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToChatResponseAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        bool threadIdPinned = false;

        // AG-UI requires the full message history on every turn, so we clear the conversation id
        // before FunctionInvokingChatClient sees it (it would skip sending history if ConversationId is set).
        // A caller-supplied ConversationId is treated as the AG-UI thread id and carried inward via
        // AdditionalProperties instead.
        var innerOptions = options;
        if (options?.ConversationId != null)
        {
            innerOptions = options.Clone();
            innerOptions.AdditionalProperties ??= [];
            innerOptions.AdditionalProperties[AGUIClientInternalKeys.ThreadId] = options.ConversationId;
            innerOptions.ConversationId = null;
        }

        // Strip ToolApprovalRequestContent/ToolApprovalResponseContent messages before they reach
        // FunctionInvokingChatClient (which would try to execute the tool locally).
        // Instead, pass the approval info through AdditionalProperties for BuildRunAgentInput to use.
        // Check the last message for fresh approval responses — older ones were already processed.
        var messagesList = messages.ToList();
        var hasCallerSuppliedResume =
            options?.RawRepresentationFactory?.Invoke(this) is RunAgentInput { Resume.Count: > 0 };
        List<ToolApprovalResponseContent>? approvalResponses = null;
        var lastMsg = messagesList.Count > 0 ? messagesList[messagesList.Count - 1] : null;
        if (lastMsg is not null)
        {
            foreach (var content in lastMsg.Contents)
            {
                if (content is ToolApprovalResponseContent response)
                {
                    approvalResponses ??= new List<ToolApprovalResponseContent>();
                    approvalResponses.Add(response);
                }
            }
        }

        if (approvalResponses is { Count: > 0 })
        {
            if (hasCallerSuppliedResume)
            {
                messagesList = RemoveContents(
                    messagesList,
                    static content => content is ToolApprovalRequestContent
                        or ToolApprovalResponseContent);
            }
            else
            {
            var requestsById = ValidateApprovalResponses(
                messagesList,
                approvalResponses);
            var clientToolNames = new HashSet<string>(
                options?.Tools?.Select(tool => tool.Name) ?? [],
                StringComparer.Ordinal);
            var clientRequestIds = new HashSet<string>(
                requestsById
                    .Where(entry => entry.Value.ToolCall is FunctionCallContent call
                        && clientToolNames.Contains(call.Name))
                    .Select(entry => entry.Key),
                StringComparer.Ordinal);
            var callIndexById = requestsById.Values
                .Select((request, index) => new { request.ToolCall.CallId, index })
                .ToDictionary(entry => entry.CallId, entry => entry.index, StringComparer.Ordinal);
            var serverApprovalResponses = approvalResponses
                .Where(response => !clientRequestIds.Contains(response.RequestId)
#pragma warning disable MEAI001
                    && requestsById[response.RequestId].RequiresConfirmation)
#pragma warning restore MEAI001
                .ToList();

            messagesList = NormalizeApprovalContents(
                messagesList,
                clientRequestIds,
                clientToolNames,
                callIndexById);
            innerOptions = (innerOptions ?? options)?.Clone() ?? new ChatOptions();
            if (serverApprovalResponses.Count > 0)
            {
                innerOptions.AdditionalProperties ??= [];
                innerOptions.AdditionalProperties[AGUIClientInternalKeys.ApprovalResponses] =
                    serverApprovalResponses;
            }
            }
        }

        var workflowCalls = GetWorkflowCalls(messagesList);
        List<WorkflowInterruptResponse>? workflowResponses = null;
        if (workflowCalls.Count > 0)
        {
            if (!hasCallerSuppliedResume)
            {
                workflowResponses = ValidateWorkflowResponses(messagesList, workflowCalls);
            }

            var workflowCallIds = new HashSet<string>(
                workflowCalls.Select(call => call.CallId),
                StringComparer.Ordinal);
            messagesList = RemoveContents(
                messagesList,
                content => content is FunctionResultContent result
                    && workflowCallIds.Contains(result.CallId));

            foreach (var workflowCall in workflowCalls)
            {
                workflowCall.InformationalOnly = true;
            }

            innerOptions = (innerOptions ?? options)?.Clone() ?? new ChatOptions();
            var workflowThreadIds = workflowCalls
                .Select(call =>
                {
                    WorkflowInterruptRegistry.TryGet(call, out _, out var threadId);
                    return threadId;
                })
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (workflowThreadIds.Count != 1)
            {
                throw new InvalidOperationException(
                    "Pending workflow function calls must belong to one AG-UI thread.");
            }
            innerOptions.AdditionalProperties ??= [];
            innerOptions.AdditionalProperties[AGUIClientInternalKeys.ThreadId] =
                workflowThreadIds[0];
            if (workflowResponses is { Count: > 0 })
            {
                innerOptions.AdditionalProperties[AGUIClientInternalKeys.WorkflowResponses] =
                    workflowResponses;
            }
        }

        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messagesList, innerOptions, cancellationToken).ConfigureAwait(false))
            {
                // The handler surfaces the resolved AG-UI thread id on the first update. Pin it on the
                // caller's options so that reusing the same ChatOptions across turns keeps a stable
                // thread id — without advertising a service ConversationId. We never promote it to
                // ConversationId, because a non-null ConversationId makes MEAI agent wrappers treat the
                // conversation as service-managed and send only deltas on the next turn, which truncates
                // history against a stateless AG-UI server (issue #4869). The thread id stays available
                // via AdditionalProperties.
                if (!threadIdPinned
                    && update.AdditionalProperties?.TryGetValue(AGUIClientInternalKeys.ThreadId, out string? resolvedThreadId) is true
                    && !string.IsNullOrEmpty(resolvedThreadId))
                {
                    threadIdPinned = true;
                    if (options is not null && options.ConversationId is null)
                    {
                        options.AdditionalProperties ??= [];
                        options.AdditionalProperties[AGUIClientInternalKeys.ThreadId] = resolvedThreadId;
                    }
                }

                // Restore workflow calls to their actionable developer-facing form after the
                // internal FICC projection treated them as informational.
                for (var i = 0; i < update.Contents.Count; i++)
                {
                    if (update.Contents[i] is FunctionCallContent functionCallContent)
                    {
                        functionCallContent.AdditionalProperties?.Remove(AGUIClientInternalKeys.ThreadId);
                        if (WorkflowInterruptRegistry.TryGet(
                            functionCallContent,
                            out var interrupt,
                            out _))
                        {
                            functionCallContent.InformationalOnly = false;
                            functionCallContent.RawRepresentation = interrupt;
                        }
                    }
                }

                // AG-UI servers are stateless: never surface a ConversationId (see issue #4869). The
                // handler already nulls it; this is a defensive guard in case an inner client sets one.
                update.ConversationId = null;

                yield return update;
            }
        }
        finally
        {
            foreach (var workflowCall in workflowCalls)
            {
                workflowCall.InformationalOnly = false;
            }
        }
    }

    private static List<ChatMessage> RemoveContents(
        List<ChatMessage> messages,
        Func<AIContent, bool> shouldRemove)
    {
        var filtered = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            var retainedContents = message.Contents.Where(content => !shouldRemove(content)).ToList();
            if (retainedContents.Count == 0)
            {
                continue;
            }

            if (retainedContents.Count == message.Contents.Count)
            {
                filtered.Add(message);
                continue;
            }

            var replacement = message.Clone();
            replacement.Contents = retainedContents;
            filtered.Add(replacement);
        }

        return filtered;
    }

    private static List<FunctionCallContent> GetWorkflowCalls(List<ChatMessage> messages)
    {
        var lastWorkflowMessageIndex = messages.FindLastIndex(message =>
            message.Contents.OfType<FunctionCallContent>().Any(call =>
                WorkflowInterruptRegistry.TryGet(call, out _, out _)));
        if (lastWorkflowMessageIndex < 0)
        {
            return [];
        }

        var firstBatchMessageIndex = lastWorkflowMessageIndex;
        while (firstBatchMessageIndex > 0
            && messages[firstBatchMessageIndex - 1].Role == ChatRole.Assistant)
        {
            firstBatchMessageIndex--;
        }

        return messages
            .Skip(firstBatchMessageIndex)
            .Take(lastWorkflowMessageIndex - firstBatchMessageIndex + 1)
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .Where(call => WorkflowInterruptRegistry.TryGet(call, out _, out _))
            .ToList();
    }

    private static List<WorkflowInterruptResponse> ValidateWorkflowResponses(
        List<ChatMessage> messages,
        List<FunctionCallContent> workflowCalls)
    {
        var callsById = new Dictionary<string, FunctionCallContent>(StringComparer.Ordinal);
        foreach (var call in workflowCalls)
        {
            if (!WorkflowInterruptRegistry.MatchesOriginalCall(call))
            {
                throw new InvalidOperationException(
                    $"Workflow function call '{call.CallId}' does not match its original request.");
            }

            if (callsById.ContainsKey(call.CallId))
            {
                throw new InvalidOperationException(
                    $"Workflow function call '{call.CallId}' appears more than once.");
            }
            callsById.Add(call.CallId, call);
        }

        var allCallsById = messages
            .SelectMany(message => message.Contents)
            .Select(content => content switch
            {
                FunctionCallContent call => call,
                ToolApprovalRequestContent { ToolCall: FunctionCallContent call } => call,
                _ => null,
            })
            .Where(call => call is not null)
            .ToDictionary(call => call!.CallId, call => call!, StringComparer.Ordinal);
        var allWorkflowCallIds = new HashSet<string>(
            allCallsById.Values
                .Where(call => WorkflowInterruptRegistry.TryGet(call, out _, out _))
                .Select(call => call.CallId),
            StringComparer.Ordinal);

        var responseResults = messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Where(result => callsById.ContainsKey(result.CallId))
            .ToList();
        var duplicateResponseId = responseResults
            .GroupBy(result => result.CallId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateResponseId is not null)
        {
            throw new InvalidOperationException(
                $"Workflow response '{duplicateResponseId}' appears more than once.");
        }

        var staleResponse = messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .FirstOrDefault(result =>
                allWorkflowCallIds.Contains(result.CallId)
                && !callsById.ContainsKey(result.CallId));
        if (staleResponse is not null)
        {
            throw new InvalidOperationException(
                $"Workflow response '{staleResponse.CallId}' is stale.");
        }

        var unknownResponse = messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .FirstOrDefault(result => !allCallsById.ContainsKey(result.CallId));
        if (unknownResponse is not null)
        {
            throw new InvalidOperationException(
                $"Workflow response '{unknownResponse.CallId}' does not match a function call.");
        }

        var resultsById = responseResults.ToDictionary(
            result => result.CallId,
            StringComparer.Ordinal);
        var missingCallIds = callsById.Keys
            .Where(callId => !resultsById.ContainsKey(callId))
            .ToList();
        if (missingCallIds.Count > 0)
        {
            throw new InvalidOperationException(
                $"Workflow responses are missing for call(s): {string.Join(", ", missingCallIds)}.");
        }

        var responses = new List<WorkflowInterruptResponse>(workflowCalls.Count);
        foreach (var call in workflowCalls)
        {
            _ = WorkflowInterruptRegistry.TryGet(call, out var interrupt, out var threadId);
            if (string.IsNullOrEmpty(threadId)
                || !string.Equals(interrupt!.ToolCallId, call.CallId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Workflow interrupt '{interrupt?.Id}' does not match function call '{call.CallId}'.");
            }

            if (interrupt.ExpiresAt is not null
                && DateTimeOffset.TryParse(interrupt.ExpiresAt, out var expiresAt)
                && expiresAt <= DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException(
                    $"Workflow interrupt '{interrupt.Id}' has expired.");
            }

            responses.Add(new WorkflowInterruptResponse
            {
                Call = call,
                Interrupt = interrupt,
                Result = resultsById[call.CallId],
                ThreadId = threadId,
            });
        }

        return responses;
    }

    private static Dictionary<string, ToolApprovalRequestContent> ValidateApprovalResponses(
        List<ChatMessage> messages,
        List<ToolApprovalResponseContent> approvalResponses)
    {
        var requestsById = messages.SelectMany(message => message.Contents)
            .OfType<ToolApprovalRequestContent>()
            .ToDictionary(request => request.RequestId, StringComparer.Ordinal);
        var responsesById = approvalResponses.ToDictionary(
            response => response.RequestId,
            StringComparer.Ordinal);
        var unansweredRequestIds = requestsById.Keys
            .Where(requestId => !responsesById.ContainsKey(requestId))
            .ToList();

        if (unansweredRequestIds.Count > 0)
        {
            throw new InvalidOperationException(
                $"Approval responses are missing for request(s): {string.Join(", ", unansweredRequestIds)}.");
        }

        foreach (var response in approvalResponses)
        {
            if (!requestsById.TryGetValue(response.RequestId, out var request)
                || request.ToolCall is not FunctionCallContent requestCall
                || response.ToolCall is not FunctionCallContent responseCall
                || !ToolCallsMatch(requestCall, responseCall))
            {
                throw new InvalidOperationException(
                    $"Approval response '{response.RequestId}' does not match its original request.");
            }
        }

        return requestsById;
    }

    private static bool ToolCallsMatch(
        FunctionCallContent requestCall,
        FunctionCallContent responseCall)
    {
        if (!string.Equals(requestCall.CallId, responseCall.CallId, StringComparison.Ordinal)
            || !string.Equals(requestCall.Name, responseCall.Name, StringComparison.Ordinal))
        {
            return false;
        }

        var typeInfo = AGUIJsonSerializerContext.Default.GetTypeInfo(
            typeof(IDictionary<string, object?>))!;
        return JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(requestCall.Arguments, typeInfo),
            JsonSerializer.SerializeToElement(responseCall.Arguments, typeInfo));
    }

    private static List<ChatMessage> NormalizeApprovalContents(
        List<ChatMessage> messages,
        HashSet<string> clientRequestIds,
        HashSet<string> clientToolNames,
        Dictionary<string, int> callIndexById)
    {
        var normalized = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            var retainedContents = new List<AIContent>(message.Contents.Count);
            foreach (var content in message.Contents)
            {
                if (content is ToolApprovalRequestContent { ToolCall: FunctionCallContent call })
                {
                    call.AdditionalProperties ??= [];
                    call.AdditionalProperties[AGUIClientInternalKeys.ApprovalCallIndex] =
                        callIndexById[call.CallId];
                    if (clientRequestIds.Contains(
                        ((ToolApprovalRequestContent)content).RequestId))
                    {
                        retainedContents.Add(content);
                    }
                    else
                    {
                        if (!clientToolNames.Contains(call.Name))
                        {
                            call.InformationalOnly = true;
                        }
                        retainedContents.Add(call);
                    }
                }
                else if (content is ToolApprovalResponseContent response)
                {
                    if (clientRequestIds.Contains(response.RequestId))
                    {
                        retainedContents.Add(content);
                    }
                }
                else if (content is not ToolApprovalRequestContent)
                {
                    retainedContents.Add(content);
                }
            }

            if (retainedContents.Count == 0)
            {
                continue;
            }

            if (retainedContents.Count == message.Contents.Count
                && retainedContents.SequenceEqual(message.Contents))
            {
                normalized.Add(message);
                continue;
            }

            var replacement = message.Clone();
            replacement.Contents = retainedContents;
            normalized.Add(replacement);
        }

        return normalized;
    }

    private static IAGUITransport GetTransport(AGUIChatClientOptions options)
    {
        ArgumentNullThrowHelper.ThrowIfNull(options);

        return options.Transport;
    }

    private static FunctionInvokingChatClient CreateInnerClient(
        IAGUITransport transport,
        JsonSerializerOptions jsonSerializerOptions)
    {
        ArgumentNullThrowHelper.ThrowIfNull(transport);

        var handler = new AGUIChatClientHandler(transport, jsonSerializerOptions);
        return new FunctionInvokingChatClient(handler)
        {
            TerminateOnUnknownCalls = true,
            FunctionInvoker = static async (context, cancellationToken) =>
            {
                var result = await context.Function.InvokeAsync(
                    context.Arguments,
                    cancellationToken).ConfigureAwait(false);

                var hasPendingWorkflowCall = context.Messages
                    .SelectMany(message => message.Contents)
                    .OfType<FunctionCallContent>()
                    .Any(call => WorkflowInterruptRegistry.TryGet(call, out _, out _));
                var hasWorkflowResponses =
                    context.Options?.AdditionalProperties?.ContainsKey(
                        AGUIClientInternalKeys.WorkflowResponses) is true;

                if (hasPendingWorkflowCall
                    && !hasWorkflowResponses
                    && context.FunctionCallIndex == context.FunctionCount - 1)
                {
                    context.Terminate = true;
                }

                return result;
            },
        };
    }

    internal static JsonSerializerOptions CombineJsonSerializerOptions(JsonSerializerOptions? jsonSerializerOptions)
    {
        if (jsonSerializerOptions == null)
        {
            return AGUIJsonSerializerContext.Default.Options;
        }

        var combinedOptions = new JsonSerializerOptions(jsonSerializerOptions);

        // AGUIJsonUtilities.DefaultTypeInfoResolver rather than the bare context: the
        // context's DefaultIgnoreCondition belongs to its own options and would not follow it
        // here, so AG-UI types resolved through the caller's options would start writing null
        // for fields that have no value. The resolver carries the rule with the metadata.
        //
        // The condition is "is it already first", not "is it present anywhere". Anything ahead
        // of it wins for AG-UI types, and two configurations a caller can plausibly arrive at
        // would otherwise silently reintroduce the nulls: a chain that already holds the bare
        // AGUIJsonSerializerContext, and a chain that holds this resolver behind a resolver
        // that answers for any type. Inserting at the front is idempotent, so calling this
        // twice does not stack duplicates.
        if (combinedOptions.TypeInfoResolverChain.FirstOrDefault() != AGUIJsonUtilities.DefaultTypeInfoResolver)
        {
            combinedOptions.TypeInfoResolverChain.Insert(0, AGUIJsonUtilities.DefaultTypeInfoResolver);
        }

        return combinedOptions;
    }

    private sealed class AGUIChatClientHandler : IChatClient
    {
        private readonly IAGUITransport _transport;
        private readonly JsonSerializerOptions _jsonSerializerOptions;

        public AGUIChatClientHandler(
            IAGUITransport transport,
            JsonSerializerOptions jsonSerializerOptions)
        {
            _transport = transport;
            _jsonSerializerOptions = jsonSerializerOptions;

            Metadata = new ChatClientMetadata("ag-ui");
        }

        public ChatClientMetadata Metadata { get; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return GetStreamingResponseAsync(messages, options, cancellationToken)
                .ToChatResponseAsync(cancellationToken);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var messagesList = messages.ToList();

            RunAgentInput? providedInput = options?.RawRepresentationFactory?.Invoke(this) as RunAgentInput;

            var threadId = (string.IsNullOrEmpty(providedInput?.ThreadId) ? null : providedInput!.ThreadId)
                ?? ExtractTemporaryThreadId(messagesList)
                ?? ExtractThreadIdFromOptions(options)
                ?? AGUIIdGenerator.NewThreadId();

            var input = BuildRunAgentInput(messagesList, options, providedInput, threadId, _jsonSerializerOptions);

            // Build set of client tool names for distinguishing client vs server tool calls
            var clientToolSet = new HashSet<string>();
            foreach (var tool in options?.Tools ?? [])
            {
                clientToolSet.Add(tool.Name);
            }

            await foreach (var update in EventStreamConverter.AsChatResponseUpdates(
                _transport.SendAsync(input, cancellationToken),
                _jsonSerializerOptions,
                clientToolSet,
                cancellationToken).ConfigureAwait(false))
            {
                // Add agui_thread_id to RunStarted updates
                if (update.RawRepresentation is RunStartedEvent)
                {
                    update.AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        [AGUIClientInternalKeys.ThreadId] = update.ConversationId ?? threadId
                    };
                }

                // Apply client, server, and workflow call distinctions. Workflow calls are
                // temporarily informational so FICC can execute all local peers; the outer
                // AGUIChatClient restores them before yielding to the developer.
                foreach (var fcc in update.Contents.OfType<FunctionCallContent>())
                {
                    if (WorkflowInterruptRegistry.TryGet(fcc, out _, out _))
                    {
                        fcc.InformationalOnly = true;
                    }
                    else if (clientToolSet.Count > 0 && clientToolSet.Contains(fcc.Name))
                    {
                        // Client tool: store thread ID so we can recover it on next turn
                        fcc.AdditionalProperties ??= [];
                        fcc.AdditionalProperties[AGUIClientInternalKeys.ThreadId] = update.ConversationId ?? threadId;
                    }
                    else
                    {
                        // Server tool: mark as informational so it won't be executed client-side
                        fcc.InformationalOnly = true;
                    }
                }

                // Remove ConversationId so FunctionInvokingChatClient sends full history
                // on subsequent iterations instead of only sending the delta
                update.ConversationId = null;

                yield return update;
            }
        }

        public void Dispose()
        {
            // HttpClient is not owned by this class
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceType == typeof(ChatClientMetadata))
            {
                return Metadata;
            }

            // Surface the AG-UI client ActivitySource so the function-invoking client that
            // AGUIChatClient owns can emit execute_tool spans for client-side tools.
            if (serviceType == typeof(ActivitySource))
            {
                return AGUIClientInstrumentation.ActivitySource;
            }

            return null;
        }

        private static RunAgentInput BuildRunAgentInput(
            List<ChatMessage> messagesList,
            ChatOptions? options,
            RunAgentInput? providedInput,
            string threadId,
            JsonSerializerOptions jsonSerializerOptions)
        {
            RestoreChatMessageToolCallOrder(messagesList);
            var input = new RunAgentInput
            {
                ThreadId = threadId,
                RunId = string.IsNullOrEmpty(providedInput?.RunId) ? AGUIIdGenerator.NewRunId() : providedInput!.RunId,
                Messages = messagesList.AsAGUIMessages().ToList(),
            };

            // Tracks whether the caller hand-supplied Resume via RawRepresentationFactory.
            // When true, the approval/interrupt translations below yield to it entirely.
            bool callerSuppliedResume = false;

            if (providedInput is not null)
            {
                if (providedInput.Messages is { Count: > 0 })
                {
                    input.Messages = providedInput.Messages;
                }

                if (providedInput.Tools is { Count: > 0 })
                {
                    input.Tools = providedInput.Tools;
                }

                if (providedInput.State is not null)
                {
                    input.State = providedInput.State;
                }

                if (!string.IsNullOrEmpty(providedInput.ParentRunId))
                {
                    input.ParentRunId = providedInput.ParentRunId;
                }

                if (providedInput.Context is { Count: > 0 })
                {
                    input.Context = providedInput.Context;
                }

                if (providedInput.ForwardedProperties.ValueKind != JsonValueKind.Undefined)
                {
                    input.ForwardedProperties = providedInput.ForwardedProperties;
                }

                // A caller-supplied Resume is authoritative: both the approval- and
                // interrupt-response translations below yield to it (see the
                // callerSuppliedResume guards), so the two resume paths stay symmetric.
                if (providedInput.Resume is { Count: > 0 })
                {
                    input.Resume = providedInput.Resume;
                    callerSuppliedResume = true;
                }
            }

            // Convert M.E.AI tools to AG-UI format
            if (input.Tools is not { Count: > 0 } && options?.Tools is { Count: > 0 })
            {
                input.Tools = options.Tools.AsAGUITools().ToList();
            }

            List<ToolApprovalResponseContent>? approvalResponses = null;
            List<WorkflowInterruptResponse>? workflowResponses = null;
            options?.AdditionalProperties?.TryGetValue(AGUIClientInternalKeys.ApprovalResponses, out approvalResponses);
            options?.AdditionalProperties?.TryGetValue(AGUIClientInternalKeys.WorkflowResponses, out workflowResponses);

            if (callerSuppliedResume)
            {
                // The caller's Resume wins, so approval/interrupt responses carried by the
                // last message are not translated over it. Those responses were already
                // stripped from the outgoing messages, so emit a diagnostic event to make
                // the drop observable instead of silent.
                int droppedApprovals = approvalResponses?.Count ?? 0;
                int droppedInterrupts = workflowResponses?.Count ?? 0;
                if (droppedApprovals > 0 || droppedInterrupts > 0)
                {
                    Activity.Current?.AddEvent(new ActivityEvent(
                        "agui.resume.caller_override_dropped_responses",
                        tags: new ActivityTagsCollection
                        {
                            { "agui.resume.dropped_approval_responses", droppedApprovals },
                            { "agui.resume.dropped_interrupt_responses", droppedInterrupts },
                        }));
                }
            }
            else
            {
                // Convert ToolApprovalResponseContent list (passed from AGUIChatClient) to resume payload
                if (approvalResponses is { Count: > 0 })
                {
                    var resumeList = new List<AGUIResume>(approvalResponses.Count);
                    foreach (var approvalResponse in approvalResponses)
                    {
                        AGUIToolCallInfo? toolCallInfo = null;
                        if (approvalResponse.ToolCall is FunctionCallContent tc)
                        {
                            toolCallInfo = new AGUIToolCallInfo
                            {
                                CallId = tc.CallId,
                                Name = tc.Name,
                                Arguments = tc.Arguments
                            };
                        }

                        // Check for a pre-computed result (from client-side tool execution)
                        string? toolResult = null;
                        if (approvalResponse.AdditionalProperties?.TryGetValue("result", out object? resultObj) is true)
                        {
                            toolResult = resultObj as string;
                        }

                        resumeList.Add(new AGUIResume
                        {
                            InterruptId = approvalResponse.RequestId,
                            Status = ResumeStatus.Resolved,
                            Payload = JsonSerializer.SerializeToElement(
                                new AGUIToolApprovalResumePayload
                                {
                                    Approved = approvalResponse.Approved,
                                    ToolCall = toolCallInfo,
                                    Result = toolResult
                                },
                                jsonSerializerOptions.GetTypeInfo(typeof(AGUIToolApprovalResumePayload)))
                        });
                    }

                    input.Resume = resumeList;
                }

                // Convert workflow FunctionResultContent responses to Resume entries,
                // appending to any approval-derived entries produced above.
                if (workflowResponses is { Count: > 0 })
                {
                    var resumeList = input.Resume is { Count: > 0 } existing
                        ? new List<AGUIResume>(existing)
                        : new List<AGUIResume>(workflowResponses.Count);

                    foreach (var workflowResponse in workflowResponses)
                    {
                        if (!string.Equals(
                            workflowResponse.ThreadId,
                            threadId,
                            StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                $"Workflow response '{workflowResponse.Result.CallId}' belongs to a different thread.");
                        }

                        var status = GetWorkflowResponseStatus(workflowResponse.Result);
                        resumeList.Add(new AGUIResume
                        {
                            InterruptId = workflowResponse.Interrupt.Id,
                            Status = status,
                            Payload = SerializeWorkflowResult(
                                workflowResponse.Result.Result,
                                jsonSerializerOptions),
                            Metadata = CreateWorkflowResumeMetadata(
                                workflowResponse,
                                workflowResponses,
                                jsonSerializerOptions),
                        });
                    }

                    input.Resume = resumeList;
                }
            }

            return input;
        }

        private static string GetWorkflowResponseStatus(FunctionResultContent result)
        {
            if (result.AdditionalProperties?.TryGetValue(
                WorkflowInterruptRegistry.ResumeStatusKey,
                out string? status) is not true)
            {
                return ResumeStatus.Resolved;
            }

            if (!string.Equals(status, ResumeStatus.Cancelled, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unsupported workflow Resume status '{status}'.");
            }

            return status;
        }

        private static JsonElement? SerializeWorkflowResult(
            object? result,
            JsonSerializerOptions jsonSerializerOptions)
        {
            return result switch
            {
                null => null,
                JsonElement element => element.Clone(),
                _ => JsonSerializer.SerializeToElement(
                    result,
                    jsonSerializerOptions.GetTypeInfo(result.GetType())),
            };
        }

        private static JsonElement CreateWorkflowResumeMetadata(
            WorkflowInterruptResponse response,
            IReadOnlyList<WorkflowInterruptResponse> pendingResponses,
            JsonSerializerOptions jsonSerializerOptions)
        {
            var metadata = response.Interrupt.Metadata is { ValueKind: JsonValueKind.Object } existing
                ? JsonNode.Parse(existing.GetRawText())!.AsObject()
                : new JsonObject();
            var arguments = JsonSerializer.SerializeToElement(
                response.Call.Arguments,
                jsonSerializerOptions.GetTypeInfo(typeof(IDictionary<string, object?>)));

            if (metadata[AGUIMetadata.ReservedKey] is not JsonObject aguiMetadata)
            {
                aguiMetadata = new JsonObject();
                metadata[AGUIMetadata.ReservedKey] = aguiMetadata;
            }

            aguiMetadata[WorkflowInterruptRegistry.ResumeMetadataKey] = new JsonObject
            {
                ["interruptId"] = response.Interrupt.Id,
                ["callId"] = response.Call.CallId,
                ["name"] = response.Call.Name,
                ["arguments"] = JsonNode.Parse(arguments.GetRawText()),
            };
            aguiMetadata[WorkflowInterruptRegistry.PendingInterruptIdsMetadataKey] = new JsonArray(
                pendingResponses.Select(pending =>
                    JsonValue.Create(pending.Interrupt.Id)).ToArray());

            return JsonDocument.Parse(metadata.ToJsonString()).RootElement.Clone();
        }

        private static void RestoreChatMessageToolCallOrder(List<ChatMessage> messages)
        {
            for (var start = 0; start < messages.Count;)
            {
                if (messages[start].Role != ChatRole.Assistant
                    || !messages[start].Contents.OfType<FunctionCallContent>().Any())
                {
                    start++;
                    continue;
                }

                var end = start + 1;
                while (end < messages.Count
                    && messages[end].Role == ChatRole.Assistant
                    && messages[end].Contents.OfType<FunctionCallContent>().Any())
                {
                    end++;
                }

                var ordered = messages.GetRange(start, end - start)
                    .OrderBy(message => message.Contents
                        .OfType<FunctionCallContent>()
                        .Select(GetApprovalCallIndex)
                        .DefaultIfEmpty(int.MaxValue)
                        .Min())
                    .ToList();
                messages.RemoveRange(start, end - start);
                messages.InsertRange(start, ordered);
                start = end;
            }
        }

        private static int GetApprovalCallIndex(FunctionCallContent call) =>
            call.AdditionalProperties?.TryGetValue(
                AGUIClientInternalKeys.ApprovalCallIndex,
                out int index) is true
                ? index
                : int.MaxValue;

        private static string? ExtractThreadIdFromOptions(ChatOptions? options)
        {
            if (options?.AdditionalProperties is null ||
                !options.AdditionalProperties.TryGetValue(AGUIClientInternalKeys.ThreadId, out string? threadId) ||
                string.IsNullOrEmpty(threadId))
            {
                return null;
            }

            return threadId;
        }

        private static string? ExtractTemporaryThreadId(List<ChatMessage> messagesList)
        {
            if (messagesList.Count < 2)
            {
                return null;
            }

            var functionCall = messagesList[messagesList.Count - 2];
            if (functionCall.Contents.Count < 1 || functionCall.Contents[0] is not FunctionCallContent content)
            {
                return null;
            }

            if (content.AdditionalProperties is null ||
                !content.AdditionalProperties.TryGetValue(AGUIClientInternalKeys.ThreadId, out string? threadId) ||
                string.IsNullOrEmpty(threadId))
            {
                return null;
            }

            return threadId;
        }
    }

}
