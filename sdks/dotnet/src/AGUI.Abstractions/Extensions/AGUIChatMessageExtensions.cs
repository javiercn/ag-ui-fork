using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AGUI.Abstractions;

/// <summary>
/// Extension methods for converting between AG-UI messages and <see cref="ChatMessage"/> instances.
/// </summary>
public static class AGUIChatMessageExtensions
{
    private static readonly ChatRole s_developerChatRole = new("developer");

    /// <summary>
    /// Converts a sequence of <see cref="AGUIMessage"/> instances to <see cref="ChatMessage"/> instances.
    /// </summary>
    /// <param name="aguiMessages">The AG-UI messages to convert.</param>
    /// <returns>A sequence of <see cref="ChatMessage"/> instances.</returns>
    public static IEnumerable<ChatMessage> AsChatMessages(this IEnumerable<AGUIMessage> aguiMessages)
    {
        foreach (var message in aguiMessages)
        {
            var role = MapChatRole(message.Role);

            if (message is AGUIUserMessage userMessage && userMessage.Content.Count > 0)
            {
                var authorName = userMessage.Name;
                var contents = new List<AIContent>();
                foreach (var inputContent in userMessage.Content)
                {
                    switch (inputContent)
                    {
                        case AGUITextInputContent textInput:
                            contents.Add(new TextContent(textInput.Text));
                            break;
                        case AGUIBinaryInputContent binaryInput:
                            if (binaryInput.Url is not null)
                            {
                                var uriContent = new UriContent(new Uri(binaryInput.Url), binaryInput.MimeType);
                                if (binaryInput.Filename is not null)
                                {
                                    uriContent.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                                    uriContent.AdditionalProperties["filename"] = binaryInput.Filename;
                                }

                                contents.Add(uriContent);
                            }
                            else if (binaryInput.Data is not null)
                            {
                                var bytes = Convert.FromBase64String(binaryInput.Data);
                                var dataContent = new DataContent(bytes, binaryInput.MimeType);
                                if (binaryInput.Filename is not null)
                                {
                                    dataContent.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                                    dataContent.AdditionalProperties["filename"] = binaryInput.Filename;
                                }

                                contents.Add(dataContent);
                            }

                            break;
                    }
                }

                yield return new ChatMessage(role, contents) { MessageId = message.Id, AuthorName = authorName };
            }
            else if (message is AGUIAssistantMessage assistantMessage && assistantMessage.ToolCalls is { Count: > 0 })
            {
                var contents = new List<AIContent>();
                if (!string.IsNullOrEmpty(assistantMessage.Content))
                {
                    contents.Add(new TextContent(assistantMessage.Content));
                }

                foreach (var toolCall in assistantMessage.ToolCalls)
                {
                    contents.Add(new FunctionCallContent(
                        toolCall.Id,
                        toolCall.Function.Name,
                        toolCall.Function.Arguments is { Length: > 0 }
                            ? (IDictionary<string, object?>?)JsonSerializer.Deserialize(
                                toolCall.Function.Arguments,
                                AGUIJsonSerializerContext.Default.GetTypeInfo(typeof(IDictionary<string, object?>))!)
                            : null));
                }

                yield return new ChatMessage(role, contents)
                {
                    MessageId = message.Id
                };
            }
            else if (message is AGUIToolMessage toolMessage)
            {
                var contents = new List<AIContent>
                {
                    new FunctionResultContent(toolMessage.ToolCallId ?? string.Empty, toolMessage.Content)
                };

                yield return new ChatMessage(role, contents)
                {
                    MessageId = message.Id
                };
            }
            else
            {
                yield return new ChatMessage(role, message.Content)
                {
                    MessageId = message.Id
                };
            }
        }
    }

    /// <summary>
    /// Converts a sequence of <see cref="ChatMessage"/> instances to <see cref="AGUIMessage"/> instances.
    /// </summary>
    /// <param name="chatMessages">The chat messages to convert.</param>
    /// <returns>A sequence of <see cref="AGUIMessage"/> instances.</returns>
    public static IEnumerable<AGUIMessage> AsAGUIMessages(this IEnumerable<ChatMessage> chatMessages)
    {
        foreach (var message in chatMessages)
        {
            AGUIMessage aguiMessage;
            if (message.Role == ChatRole.User)
            {
                var userMsg = new AGUIUserMessage { Name = message.AuthorName };
                foreach (var content in message.Contents)
                {
                    switch (content)
                    {
                        case TextContent textContent:
                            userMsg.Content.Add(new AGUITextInputContent { Text = textContent.Text ?? string.Empty });
                            break;
                        case DataContent dataContent:
                            userMsg.Content.Add(new AGUIBinaryInputContent
                            {
                                MimeType = dataContent.MediaType ?? string.Empty,
                                Data = dataContent.Data is { Length: > 0 } ? Convert.ToBase64String(dataContent.Data.ToArray()) : null,
                                Filename = dataContent.AdditionalProperties?.TryGetValue("filename", out string? fn) == true ? fn : null
                            });
                            break;
                        case UriContent uriContent:
                            userMsg.Content.Add(new AGUIBinaryInputContent
                            {
                                MimeType = uriContent.MediaType ?? string.Empty,
                                Url = uriContent.Uri?.ToString(),
                                Filename = uriContent.AdditionalProperties?.TryGetValue("filename", out string? fn2) == true ? fn2 : null
                            });
                            break;
                        default:
                            userMsg.Content.Add(new AGUITextInputContent { Text = content.ToString() ?? string.Empty });
                            break;
                    }
                }

                aguiMessage = userMsg;
            }
            else if (message.Role == ChatRole.Assistant)
            {
                var functionCalls = message.Contents.OfType<FunctionCallContent>().ToList();
                var assistantMsg = new AGUIAssistantMessage { Content = message.Text ?? string.Empty };
                if (functionCalls.Count > 0)
                {
                    assistantMsg.ToolCalls = new List<AGUIToolCall>();
                    foreach (var fc in functionCalls)
                    {
                        assistantMsg.ToolCalls.Add(new AGUIToolCall
                        {
                            Id = fc.CallId ?? string.Empty,
                            Type = "function",
                            Function = new AGUIToolCallFunction
                            {
                                Name = fc.Name ?? string.Empty,
                                Arguments = fc.Arguments is not null
                                    ? JsonSerializer.Serialize(
                                        fc.Arguments,
                                        AGUIJsonSerializerContext.Default.GetTypeInfo(typeof(IDictionary<string, object?>))!)
                                    : string.Empty
                            }
                        });
                    }
                }

                aguiMessage = assistantMsg;
            }
            else if (message.Role == ChatRole.System)
            {
                aguiMessage = new AGUISystemMessage { Content = message.Text ?? string.Empty };
            }
            else if (message.Role == ChatRole.Tool)
            {
                var functionResults = message.Contents.OfType<FunctionResultContent>().ToList();
                if (functionResults.Count == 0)
                {
                    // No structured result; fall back to the raw message text under its message id.
                    yield return new AGUIToolMessage
                    {
                        Id = message.MessageId,
                        Content = message.Text ?? string.Empty
                    };
                }
                else
                {
                    // AG-UI models each tool result as a separate ToolMessage keyed on its tool
                    // call id (the response side likewise sets TOOL_CALL_RESULT.messageId =
                    // toolCallId). MEAI batches parallel tool results into a single tool
                    // ChatMessage, so emit one AGUIToolMessage per result to preserve them all.
                    foreach (var functionResult in functionResults)
                    {
                        yield return new AGUIToolMessage
                        {
                            Id = functionResult.CallId,
                            ToolCallId = functionResult.CallId,
                            Content = SerializeFunctionResult(functionResult, message.Text)
                        };
                    }
                }

                continue;
            }
            else
            {
                aguiMessage = new AGUIUserMessage
                {
                    Content = [new AGUITextInputContent { Text = message.Text ?? string.Empty }]
                };
            }

            aguiMessage.Id = message.MessageId;
            yield return aguiMessage;
        }
    }

    /// <summary>
    /// Maps an AG-UI role string to a <see cref="ChatRole"/>.
    /// </summary>
    /// <param name="role">The AG-UI role string.</param>
    /// <returns>The corresponding <see cref="ChatRole"/>.</returns>
    public static ChatRole MapChatRole(string role) =>
        string.Equals(role, AGUIRoles.System, StringComparison.OrdinalIgnoreCase) ? ChatRole.System :
        string.Equals(role, AGUIRoles.User, StringComparison.OrdinalIgnoreCase) ? ChatRole.User :
        string.Equals(role, AGUIRoles.Assistant, StringComparison.OrdinalIgnoreCase) ? ChatRole.Assistant :
        string.Equals(role, AGUIRoles.Developer, StringComparison.OrdinalIgnoreCase) ? s_developerChatRole :
        string.Equals(role, AGUIRoles.Tool, StringComparison.OrdinalIgnoreCase) ? ChatRole.Tool :
        throw new InvalidOperationException($"Unknown chat role: {role}");

    private static string SerializeFunctionResult(FunctionResultContent functionResult, string? fallbackText)
    {
        switch (functionResult.Result)
        {
            case string stringResult:
                return stringResult;
            case JsonElement jsonElement:
                return jsonElement.GetRawText();
            case IDictionary<string, object?>:
                return JsonSerializer.Serialize(
                    functionResult.Result,
                    AGUIJsonSerializerContext.Default.GetTypeInfo(typeof(IDictionary<string, object?>))!);
            case not null:
                var resultTypeInfo = AGUIJsonSerializerContext.Default.GetTypeInfo(functionResult.Result.GetType());
                return resultTypeInfo is not null
                    ? JsonSerializer.Serialize(functionResult.Result, resultTypeInfo)
                    : functionResult.Result.ToString() ?? string.Empty;
            default:
                return fallbackText ?? string.Empty;
        }
    }
}
