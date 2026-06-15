using System.ComponentModel;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Hosting.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Step04_HumanInLoop;

internal static class AGUIEndpoint
{
    // Tools that require human approval before execution
    private static readonly HashSet<string> s_approvalRequiredTools = new(StringComparer.Ordinal)
    {
        "ApproveExpenseReport"
    };

    internal static IEndpointConventionBuilder MapAGUI(
        this IEndpointRouteBuilder endpoints,
        string pattern)
    {
        return endpoints.MapPost(pattern, (
            [FromBody] RunAgentInput input,
            [FromServices] IChatClient chatClient,
            [FromServices] IOptions<JsonOptions> jsonOptions,
            CancellationToken cancellationToken) =>
        {
            var jsonSerializerOptions = jsonOptions.Value.SerializerOptions;

            var ctx = input.ToChatRequestContext(jsonSerializerOptions);

            // Process any incoming request_approval results: if approved, execute the real tool
            // and replace request_approval call/result with the original tool call/result in messages
            ProcessPendingApprovals(ctx.Messages, jsonSerializerOptions);

            // Add the server tool alongside any approval-wrapped client tools already
            // installed by ToChatRequestContext.
            ctx.ChatOptions.Tools ??= [];
            ctx.ChatOptions.Tools.Add(
                AIFunctionFactory.Create(
                    ApproveExpenseReport,
                    serializerOptions: jsonSerializerOptions));

            var updates = chatClient.GetStreamingResponseAsync(ctx.Messages, ctx.ChatOptions, cancellationToken);

            // Wrap approval-required tool calls as request_approval before converting to AG-UI events
            var wrappedUpdates = WrapApprovalRequiredToolCalls(updates, jsonSerializerOptions, cancellationToken);

            var events = wrappedUpdates.AsAGUIEventStreamAsync(ctx, cancellationToken);

            return TypedResults.ServerSentEvents(WrapAsSseItems(events, cancellationToken));
        });
    }

    [Description("Approve the expense report.")]
    internal static string ApproveExpenseReport(
        [Description("The expense report ID to approve")] string expenseReportId)
    {
        return $"Expense report {expenseReportId} has been approved and processed.";
    }

    private static void ProcessPendingApprovals(
        List<ChatMessage> messages,
        JsonSerializerOptions jsonSerializerOptions)
    {
        // Find request_approval tool calls and their results in the message history
        // If approved, execute the original tool and replace messages accordingly
        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            List<AIContent>? transformedContents = null;

            for (int j = 0; j < message.Contents.Count; j++)
            {
                var content = message.Contents[j];

                if (content is FunctionCallContent { Name: "request_approval" } approvalCall)
                {
                    transformedContents ??= CopyContentsUpToIndex(message.Contents, j);

                    // Deserialize the approval request to get the original function details
                    var approvalRequest = DeserializeApprovalRequest(approvalCall.Arguments, jsonSerializerOptions);
                    if (approvalRequest != null)
                    {
                        // Replace with the original function call
                        var originalArgs = approvalRequest.FunctionArguments != null
                            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(
                                approvalRequest.FunctionArguments.Value.GetRawText(),
                                jsonSerializerOptions)
                            : null;

                        transformedContents.Add(new FunctionCallContent(
                            callId: approvalRequest.ApprovalId,
                            name: approvalRequest.FunctionName,
                            arguments: originalArgs));
                    }
                }
                else if (content is FunctionResultContent resultContent)
                {
                    // Check if this is a result for a request_approval call
                    var approvalResponse = DeserializeApprovalResponse(resultContent, jsonSerializerOptions);
                    if (approvalResponse != null)
                    {
                        transformedContents ??= CopyContentsUpToIndex(message.Contents, j);

                        if (approvalResponse.Approved)
                        {
                            // Execute the original tool
                            var toolResult = ExecuteApprovedTool(
                                approvalResponse.ApprovalId,
                                messages,
                                jsonSerializerOptions);

                            transformedContents.Add(new FunctionResultContent(
                                callId: approvalResponse.ApprovalId,
                                result: toolResult));
                        }
                        else
                        {
                            // Tool was rejected
                            transformedContents.Add(new FunctionResultContent(
                                callId: approvalResponse.ApprovalId,
                                result: "The user declined to approve this operation."));
                        }
                    }
                    else
                    {
                        transformedContents?.Add(content);
                    }
                }
                else
                {
                    transformedContents?.Add(content);
                }
            }

            if (transformedContents != null)
            {
                messages[i] = new ChatMessage(message.Role, transformedContents)
                {
                    AuthorName = message.AuthorName,
                    MessageId = message.MessageId,
                    CreatedAt = message.CreatedAt,
                    RawRepresentation = message.RawRepresentation,
                    AdditionalProperties = message.AdditionalProperties
                };
            }
        }
    }

    private static string ExecuteApprovedTool(
        string approvalId,
        List<ChatMessage> messages,
        JsonSerializerOptions jsonSerializerOptions)
    {
        // Find the original function call that matches this approval ID
        foreach (var msg in messages)
        {
            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fcc && fcc.CallId == approvalId)
                {
                    // Execute the function based on its name
                    if (string.Equals(fcc.Name, "ApproveExpenseReport", StringComparison.Ordinal))
                    {
                        var expenseReportId = fcc.Arguments?.TryGetValue("expenseReportId", out var id) == true
                            ? id?.ToString() ?? "unknown"
                            : "unknown";
                        return ApproveExpenseReport(expenseReportId);
                    }
                }
            }
        }

        return "Error: Could not find the original function call for this approval.";
    }

    private static ApprovalRequest? DeserializeApprovalRequest(
        IDictionary<string, object?>? arguments,
        JsonSerializerOptions jsonSerializerOptions)
    {
        if (arguments == null || !arguments.TryGetValue("request", out var reqObj))
        {
            return null;
        }

        if (reqObj is JsonElement je)
        {
            return (ApprovalRequest?)je.Deserialize(
                jsonSerializerOptions.GetTypeInfo(typeof(ApprovalRequest)));
        }

        return null;
    }

    private static ApprovalResponse? DeserializeApprovalResponse(
        FunctionResultContent resultContent,
        JsonSerializerOptions jsonSerializerOptions)
    {
        if (resultContent.Result is JsonElement je)
        {
            return (ApprovalResponse?)je.Deserialize(
                jsonSerializerOptions.GetTypeInfo(typeof(ApprovalResponse)));
        }

        if (resultContent.Result is string str)
        {
            return (ApprovalResponse?)JsonSerializer.Deserialize(
                str, jsonSerializerOptions.GetTypeInfo(typeof(ApprovalResponse)));
        }

        return null;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> WrapApprovalRequiredToolCalls(
        IAsyncEnumerable<ChatResponseUpdate> updates,
        JsonSerializerOptions jsonSerializerOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in updates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            bool hasApprovalContent = false;
            for (int i = 0; i < update.Contents.Count; i++)
            {
                if (update.Contents[i] is FunctionCallContent fcc && s_approvalRequiredTools.Contains(fcc.Name))
                {
                    hasApprovalContent = true;
                    break;
                }
            }

            if (!hasApprovalContent)
            {
                yield return update;
                continue;
            }

            // Transform approval-required tool calls into request_approval calls
            var newContents = new List<AIContent>();
            foreach (var content in update.Contents)
            {
                if (content is FunctionCallContent fcc && s_approvalRequiredTools.Contains(fcc.Name))
                {
                    var approvalRequest = new ApprovalRequest
                    {
                        ApprovalId = fcc.CallId,
                        FunctionName = fcc.Name,
                        FunctionArguments = fcc.Arguments != null
                            ? JsonSerializer.SerializeToElement(
                                fcc.Arguments,
                                jsonSerializerOptions.GetTypeInfo(typeof(IDictionary<string, object?>)))
                            : null,
                        Message = $"Approve execution of '{fcc.Name}'?"
                    };

                    newContents.Add(new FunctionCallContent(
                        callId: fcc.CallId,
                        name: "request_approval",
                        arguments: new Dictionary<string, object?>
                        {
                            ["request"] = approvalRequest
                        }));
                }
                else
                {
                    newContents.Add(content);
                }
            }

            yield return new ChatResponseUpdate
            {
                Role = update.Role,
                Contents = newContents,
                MessageId = update.MessageId,
                AuthorName = update.AuthorName,
                CreatedAt = update.CreatedAt,
                RawRepresentation = update.RawRepresentation,
                ResponseId = update.ResponseId,
                AdditionalProperties = update.AdditionalProperties,
                FinishReason = update.FinishReason,
                ModelId = update.ModelId,
            };
        }
    }

    private static List<AIContent> CopyContentsUpToIndex(IList<AIContent> contents, int index)
    {
        var result = new List<AIContent>(index);
        for (int i = 0; i < index; i++)
        {
            result.Add(contents[i]);
        }
        return result;
    }

    private static async IAsyncEnumerable<SseItem<BaseEvent>> WrapAsSseItems(
        IAsyncEnumerable<BaseEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new SseItem<BaseEvent>(evt);
        }
    }
}
