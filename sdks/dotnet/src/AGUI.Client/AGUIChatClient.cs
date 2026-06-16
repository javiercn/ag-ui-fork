using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AGUI.Abstractions;
using AGUI.Client.Internal;
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
    /// <param name="httpClient">The HTTP client to use for communication.</param>
    /// <param name="endpoint">The AG-UI server endpoint URL.</param>
    /// <param name="jsonSerializerOptions">Optional JSON serializer options for custom type support.</param>
    public AGUIChatClient(HttpClient httpClient, string endpoint, JsonSerializerOptions? jsonSerializerOptions = null)
        : base(CreateInnerClient(CreateHttpTransport(httpClient, endpoint), CombineJsonSerializerOptions(jsonSerializerOptions)))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AGUIChatClient"/> class.
    /// </summary>
    /// <param name="transport">The transport to use for sending AG-UI protocol requests.</param>
    public AGUIChatClient(IAGUITransport transport)
        : base(CreateInnerClient(transport, CombineJsonSerializerOptions(null)))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AGUIChatClient"/> class.
    /// </summary>
    /// <param name="transport">The transport to use for sending AG-UI protocol requests.</param>
    /// <param name="jsonSerializerOptions">JSON serializer options for custom type support.</param>
    public AGUIChatClient(IAGUITransport transport, JsonSerializerOptions jsonSerializerOptions)
        : base(CreateInnerClient(transport, CombineJsonSerializerOptions(jsonSerializerOptions)))
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
        ChatResponseUpdate? firstUpdate = null;
        string? conversationId = null;

        // AG-UI requires the full message history on every turn, so we clear the conversation id
        // before FunctionInvokingChatClient sees it (it would skip sending history if ConversationId is set).
        // We store it in AdditionalProperties for the inner handler to use.
        var innerOptions = options;
        if (options?.ConversationId != null)
        {
            conversationId = options.ConversationId;
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
        List<ToolApprovalResponseContent>? approvalResponses = null;
        List<InterruptResponseContent>? interruptResponses = null;
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
                else if (content is InterruptResponseContent interruptResponse)
                {
                    interruptResponses ??= new List<InterruptResponseContent>();
                    interruptResponses.Add(interruptResponse);
                }
            }
        }

        if (approvalResponses is { Count: > 0 })
        {
            var filtered = new List<ChatMessage>();
            foreach (var message in messagesList)
            {
                if (message.Contents.Any(c => c is ToolApprovalRequestContent || c is ToolApprovalResponseContent))
                {
                    continue;
                }

                filtered.Add(message);
            }

            messagesList = filtered;
            innerOptions = (innerOptions ?? options)?.Clone() ?? new ChatOptions();
            innerOptions.AdditionalProperties ??= [];
            innerOptions.AdditionalProperties[AGUIClientInternalKeys.ApprovalResponses] = approvalResponses;
        }

        if (interruptResponses is { Count: > 0 })
        {
            var filtered = new List<ChatMessage>();
            foreach (var message in messagesList)
            {
                if (message.Contents.Any(c => c is InterruptRequestContent || c is InterruptResponseContent))
                {
                    continue;
                }

                filtered.Add(message);
            }

            messagesList = filtered;
            innerOptions = (innerOptions ?? options)?.Clone() ?? new ChatOptions();
            innerOptions.AdditionalProperties ??= [];
            innerOptions.AdditionalProperties[AGUIClientInternalKeys.InterruptResponses] = interruptResponses;
        }

        await foreach (var update in base.GetStreamingResponseAsync(messagesList, innerOptions, cancellationToken).ConfigureAwait(false))
        {
            if (conversationId == null && firstUpdate == null)
            {
                firstUpdate = update;
                if (firstUpdate.AdditionalProperties?.TryGetValue(AGUIClientInternalKeys.ThreadId, out string? threadId) is true)
                {
                    conversationId = threadId;
                }
            }

            // Clean up agui_thread_id from function call additional properties
            for (var i = 0; i < update.Contents.Count; i++)
            {
                if (update.Contents[i] is FunctionCallContent functionCallContent)
                {
                    functionCallContent.AdditionalProperties?.Remove(AGUIClientInternalKeys.ThreadId);
                }
            }

            // Note: we must NOT mutate update.ConversationId here because FICC holds
            // references to yielded updates and uses them in ToChatResponse(). Mutating
            // would cause FICC to see a non-null ConversationId and send only delta on
            // subsequent iterations. Instead, we create a new update with ConversationId set.
            if (conversationId != null)
            {
                var wrappedUpdate = new ChatResponseUpdate
                {
                    ConversationId = conversationId,
                    Role = update.Role,
                    RawRepresentation = update.RawRepresentation,
                    ModelId = update.ModelId,
                    AdditionalProperties = update.AdditionalProperties,
                    Contents = update.Contents,
                    ResponseId = update.ResponseId,
                    MessageId = update.MessageId,
                    FinishReason = update.FinishReason,
                    CreatedAt = update.CreatedAt,
                    AuthorName = update.AuthorName,
                };
                yield return wrappedUpdate;
            }
            else
            {
                yield return update;
            }
        }
    }

    private static AGUIHttpTransport CreateHttpTransport(HttpClient httpClient, string endpoint)
    {
#if NET7_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpoint);
#else
        if (httpClient == null)
        {
            throw new ArgumentNullException(nameof(httpClient));
        }

        if (endpoint == null)
        {
            throw new ArgumentNullException(nameof(endpoint));
        }
#endif

        return new AGUIHttpTransport(httpClient, endpoint);
    }

    private static FunctionInvokingChatClient CreateInnerClient(
        IAGUITransport transport,
        JsonSerializerOptions jsonSerializerOptions)
    {
#if NET7_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(transport);
#else
        if (transport == null)
        {
            throw new ArgumentNullException(nameof(transport));
        }
#endif

        var handler = new AGUIChatClientHandler(transport, jsonSerializerOptions);
        return new FunctionInvokingChatClient(handler);
    }

    private static JsonSerializerOptions CombineJsonSerializerOptions(JsonSerializerOptions? jsonSerializerOptions)
    {
        if (jsonSerializerOptions == null)
        {
            return AGUIJsonSerializerContext.Default.Options;
        }

        var combinedOptions = new JsonSerializerOptions(jsonSerializerOptions);

        if (!combinedOptions.TypeInfoResolverChain.Any(r => r == AGUIJsonSerializerContext.Default))
        {
            combinedOptions.TypeInfoResolverChain.Insert(0, AGUIJsonSerializerContext.Default);
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
                _transport.SendAsync(input, cancellationToken), _jsonSerializerOptions, cancellationToken).ConfigureAwait(false))
            {
                // Add agui_thread_id to RunStarted updates
                if (update.RawRepresentation is RunStartedEvent)
                {
                    update.AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        [AGUIClientInternalKeys.ThreadId] = update.ConversationId ?? threadId
                    };
                }

                // Apply client vs server tool call distinction
                var fcc = update.Contents.OfType<FunctionCallContent>().FirstOrDefault();
                if (fcc != null)
                {
                    if (clientToolSet.Count > 0 && clientToolSet.Contains(fcc.Name))
                    {
                        // Client tool: store thread ID so we can recover it on next turn
                        fcc.AdditionalProperties ??= [];
                        fcc.AdditionalProperties[AGUIClientInternalKeys.ThreadId] = update.ConversationId ?? threadId;
                    }
                    else
                    {
                        // Server tool: mark as informational so it won't be executed client-side
                        for (var i = 0; i < update.Contents.Count; i++)
                        {
                            if (update.Contents[i] is FunctionCallContent serverFcc)
                            {
                                serverFcc.InformationalOnly = true;
                            }
                        }
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

            return null;
        }

        private static RunAgentInput BuildRunAgentInput(
            List<ChatMessage> messagesList,
            ChatOptions? options,
            RunAgentInput? providedInput,
            string threadId,
            JsonSerializerOptions jsonSerializerOptions)
        {
            var input = new RunAgentInput
            {
                ThreadId = threadId,
                RunId = string.IsNullOrEmpty(providedInput?.RunId) ? AGUIIdGenerator.NewRunId() : providedInput!.RunId,
                Messages = messagesList.AsAGUIMessages().ToList(),
            };

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
            }

            // Convert M.E.AI tools to AG-UI format
            if (input.Tools is not { Count: > 0 } && options?.Tools is { Count: > 0 })
            {
                input.Tools = options.Tools.AsAGUITools().ToList();
            }

            // Convert ToolApprovalResponseContent list (passed from AGUIChatClient) to resume payload
            if (input.Resume is null &&
                options?.AdditionalProperties?.TryGetValue(AGUIClientInternalKeys.ApprovalResponses, out List<ToolApprovalResponseContent>? approvalResponses) is true
                && approvalResponses is { Count: > 0 })
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

            // Convert InterruptResponseContent list to resume entries.
            if (options?.AdditionalProperties?.TryGetValue(AGUIClientInternalKeys.InterruptResponses, out List<InterruptResponseContent>? interruptResponses) is true
                && interruptResponses is { Count: > 0 })
            {
                var resumeList = input.Resume is { Count: > 0 } existing
                    ? new List<AGUIResume>(existing)
                    : new List<AGUIResume>(interruptResponses.Count);

                foreach (var ir in interruptResponses)
                {
                    resumeList.Add(new AGUIResume
                    {
                        InterruptId = ir.RequestId,
                        Status = ResumeStatus.Resolved,
                        Payload = ir.Payload,
                    });
                }

                input.Resume = resumeList;
            }

            return input;
        }

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
