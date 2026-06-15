using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;
using Xunit;

namespace AGUI.Abstractions.UnitTests;

public sealed class AGUIChatMessageExtensionsTest
{
    [Fact]
    public void AsChatMessages_UserMessageWithName_SetsAuthorName()
    {
        var aguiMessages = new AGUIMessage[]
        {
            new AGUIUserMessage
            {
                Id = "msg-1",
                Name = "Alice",
                Content = [new AGUITextInputContent { Text = "Hello" }]
            }
        };

        var chatMessages = aguiMessages.AsChatMessages().ToList();

        Assert.Single(chatMessages);
        Assert.Equal("Alice", chatMessages[0].AuthorName);
        Assert.Equal(ChatRole.User, chatMessages[0].Role);
    }

    [Fact]
    public void AsChatMessages_UserMessageWithoutName_AuthorNameIsNull()
    {
        var aguiMessages = new AGUIMessage[]
        {
            new AGUIUserMessage
            {
                Id = "msg-1",
                Content = [new AGUITextInputContent { Text = "Hello" }]
            }
        };

        var chatMessages = aguiMessages.AsChatMessages().ToList();

        Assert.Single(chatMessages);
        Assert.Null(chatMessages[0].AuthorName);
    }

    [Fact]
    public void AsAGUIMessages_UserWithAuthorName_SetsName()
    {
        var chatMessages = new ChatMessage[]
        {
            new ChatMessage(ChatRole.User, "Hello") { AuthorName = "Bob" }
        };

        var aguiMessages = chatMessages.AsAGUIMessages().ToList();

        Assert.Single(aguiMessages);
        var userMsg = Assert.IsType<AGUIUserMessage>(aguiMessages[0]);
        Assert.Equal("Bob", userMsg.Name);
    }

    [Fact]
    public void AsAGUIMessages_UserWithoutAuthorName_NameIsNull()
    {
        var chatMessages = new ChatMessage[]
        {
            new ChatMessage(ChatRole.User, "Hello")
        };

        var aguiMessages = chatMessages.AsAGUIMessages().ToList();

        Assert.Single(aguiMessages);
        var userMsg = Assert.IsType<AGUIUserMessage>(aguiMessages[0]);
        Assert.Null(userMsg.Name);
    }

    [Fact]
    public void RoundTrip_UserMessageWithName_PreservesName()
    {
        var original = new ChatMessage(ChatRole.User, "Hello") { AuthorName = "Charlie", MessageId = "msg-1" };

        var aguiMessages = new[] { original }.AsAGUIMessages().ToList();
        var roundTripped = aguiMessages.AsChatMessages().ToList();

        Assert.Single(roundTripped);
        Assert.Equal("Charlie", roundTripped[0].AuthorName);
        Assert.Equal("msg-1", roundTripped[0].MessageId);
    }

    [Fact]
    public void AsChatMessages_ToolMessageWithToolCallId_CreatesFunctionResultContent()
    {
        var aguiMessages = new AGUIMessage[]
        {
            new AGUIToolMessage
            {
                Id = "tool-msg-1",
                ToolCallId = "tc_1",
                Content = "72°F, sunny"
            }
        };

        var chatMessages = aguiMessages.AsChatMessages().ToList();

        Assert.Single(chatMessages);
        Assert.Equal(ChatRole.Tool, chatMessages[0].Role);
        var resultContent = Assert.Single(chatMessages[0].Contents.OfType<FunctionResultContent>());
        Assert.Equal("tc_1", resultContent.CallId);
        Assert.Equal("72°F, sunny", resultContent.Result as string);
    }

    [Fact]
    public void AsAGUIMessages_ToolMessage_CreatesAGUIToolMessage()
    {
        var chatMessages = new ChatMessage[]
        {
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("tc_1", "72°F, sunny")])
            {
                MessageId = "tool-msg-1"
            }
        };

        var aguiMessages = chatMessages.AsAGUIMessages().ToList();

        Assert.Single(aguiMessages);
        var toolMsg = Assert.IsType<AGUIToolMessage>(aguiMessages[0]);
        Assert.Equal("tc_1", toolMsg.ToolCallId);
        Assert.Equal("72°F, sunny", toolMsg.Content);
        Assert.Equal("tool-msg-1", toolMsg.Id);
    }

    [Fact]
    public void AsAGUIMessages_ToolResultObjectContent_SerializesToJson()
    {
        var resultObject = new Dictionary<string, object?> { ["temperature"] = 72, ["condition"] = "sunny" };
        var chatMessages = new ChatMessage[]
        {
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("tc_1", resultObject)])
        };

        var aguiMessages = chatMessages.AsAGUIMessages().ToList();

        var toolMsg = Assert.IsType<AGUIToolMessage>(aguiMessages[0]);
        Assert.Equal("tc_1", toolMsg.ToolCallId);

        // Verify the content is valid JSON, not a type name
        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(toolMsg.Content);
        Assert.NotNull(parsed);
        Assert.Equal("sunny", parsed!["condition"].GetString());
    }

    [Fact]
    public void AsChatMessages_AssistantMessageWithToolCalls_CreatesFunctionCallContent()
    {
        var aguiMessages = new AGUIMessage[]
        {
            new AGUIAssistantMessage
            {
                Id = "asst-1",
                Content = string.Empty,
                ToolCalls = new List<AGUIToolCall>
                {
                    new AGUIToolCall
                    {
                        Id = "tc_1",
                        Type = "function",
                        Function = new AGUIToolCallFunction
                        {
                            Name = "get_weather",
                            Arguments = "{\"city\":\"NYC\"}"
                        }
                    }
                }
            }
        };

        var chatMessages = aguiMessages.AsChatMessages().ToList();

        Assert.Single(chatMessages);
        Assert.Equal(ChatRole.Assistant, chatMessages[0].Role);
        var functionCall = Assert.Single(chatMessages[0].Contents.OfType<FunctionCallContent>());
        Assert.Equal("tc_1", functionCall.CallId);
        Assert.Equal("get_weather", functionCall.Name);
        Assert.NotNull(functionCall.Arguments);
        Assert.Equal("NYC", functionCall.Arguments!["city"]!.ToString());
    }

    [Fact]
    public void AsAGUIMessages_AssistantWithFunctionCallContent_CreatesToolCalls()
    {
        var chatMessages = new ChatMessage[]
        {
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("tc_1", "get_weather",
                    new Dictionary<string, object?> { ["city"] = "NYC" })
            ])
        };

        var aguiMessages = chatMessages.AsAGUIMessages().ToList();

        var assistantMsg = Assert.IsType<AGUIAssistantMessage>(aguiMessages[0]);
        Assert.NotNull(assistantMsg.ToolCalls);
        var toolCall = Assert.Single(assistantMsg.ToolCalls!);
        Assert.Equal("tc_1", toolCall.Id);
        Assert.Equal("function", toolCall.Type);
        Assert.Equal("get_weather", toolCall.Function.Name);
        Assert.Contains("NYC", toolCall.Function.Arguments);
    }

    [Fact]
    public void RoundTrip_MultiTurnToolCallConversation_PreservesStructure()
    {
        // Simulate: User -> Assistant(toolCall) -> Tool(result) -> Assistant(response)
        var original = new ChatMessage[]
        {
            new ChatMessage(ChatRole.User, "What's the weather?") { MessageId = "msg-1" },
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("tc_1", "get_weather",
                    new Dictionary<string, object?> { ["city"] = "NYC" })
            ]) { MessageId = "msg-2" },
            new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent("tc_1", "72°F, sunny")
            ]) { MessageId = "msg-3" },
            new ChatMessage(ChatRole.Assistant, "The weather in NYC is 72°F and sunny!") { MessageId = "msg-4" }
        };

        var aguiMessages = original.AsAGUIMessages().ToList();
        var roundTripped = aguiMessages.AsChatMessages().ToList();

        Assert.Equal(4, roundTripped.Count);

        // User message
        Assert.Equal(ChatRole.User, roundTripped[0].Role);

        // Assistant with tool call
        Assert.Equal(ChatRole.Assistant, roundTripped[1].Role);
        var functionCall = Assert.Single(roundTripped[1].Contents.OfType<FunctionCallContent>());
        Assert.Equal("tc_1", functionCall.CallId);
        Assert.Equal("get_weather", functionCall.Name);

        // Tool result
        Assert.Equal(ChatRole.Tool, roundTripped[2].Role);
        var functionResult = Assert.Single(roundTripped[2].Contents.OfType<FunctionResultContent>());
        Assert.Equal("tc_1", functionResult.CallId);
        Assert.Equal("72°F, sunny", functionResult.Result as string);

        // Final assistant response
        Assert.Equal(ChatRole.Assistant, roundTripped[3].Role);
        Assert.Equal("The weather in NYC is 72°F and sunny!", roundTripped[3].Text);
    }

    [Fact]
    public void RoundTrip_ToolMessageJsonSerialization_PreservesContent()
    {
        var options = AGUIJsonSerializerContext.Default.Options;

        var toolMessage = new AGUIToolMessage
        {
            Id = "tool-1",
            ToolCallId = "tc_1",
            Content = "plain string result"
        };

        var json = JsonSerializer.Serialize<AGUIMessage>(toolMessage, options);
        var deserialized = JsonSerializer.Deserialize<AGUIMessage>(json, options);

        var roundTripped = Assert.IsType<AGUIToolMessage>(deserialized);
        Assert.Equal("tool", roundTripped.Role);
        Assert.Equal("tc_1", roundTripped.ToolCallId);
        Assert.Equal("plain string result", roundTripped.Content);
    }

    [Fact]
    public void RoundTrip_AssistantMessageWithToolCalls_JsonSerialization()
    {
        var options = AGUIJsonSerializerContext.Default.Options;

        var assistantMessage = new AGUIAssistantMessage
        {
            Id = "asst-1",
            Content = string.Empty,
            ToolCalls = new List<AGUIToolCall>
            {
                new AGUIToolCall
                {
                    Id = "tc_1",
                    Type = "function",
                    Function = new AGUIToolCallFunction
                    {
                        Name = "get_weather",
                        Arguments = "{\"city\":\"NYC\"}"
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize<AGUIMessage>(assistantMessage, options);
        var deserialized = JsonSerializer.Deserialize<AGUIMessage>(json, options);

        var roundTripped = Assert.IsType<AGUIAssistantMessage>(deserialized);
        Assert.NotNull(roundTripped.ToolCalls);
        var toolCall = Assert.Single(roundTripped.ToolCalls!);
        Assert.Equal("tc_1", toolCall.Id);
        Assert.Equal("get_weather", toolCall.Function.Name);
        Assert.Equal("{\"city\":\"NYC\"}", toolCall.Function.Arguments);
    }
}
