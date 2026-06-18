using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using Xunit;

namespace AGUI.Abstractions.UnitTests;

// Validation tests for microsoft/agent-framework AG-UI issues that live at the Abstractions
// layer. A test for a confirmed-open gap carries [Fact(Skip=...)] referencing the issue and
// its analysis doc under sdks/dotnet/maf-issue-analysis/; remove Skip when the gap is closed.
public class MafIssueAbstractionsValidationTests
{
    // Issue #2699 — when a client (e.g. @ag-ui/client) splits parallel tool calls into
    // separate assistant messages, AsChatMessages coalesces them into a single assistant
    // message so the reconstructed OpenAI tool_call history is valid.
    [Fact]
    public void AsChatMessages_ParallelToolCalls_CoalescedIntoSingleAssistantMessage()
    {
        var aguiMessages = new AGUIMessage[]
        {
            new AGUIAssistantMessage
            {
                Id = "asst-1",
                Content = string.Empty,
                ToolCalls = new List<AGUIToolCall>
                {
                    new AGUIToolCall { Id = "tc_1", Type = "function", Function = new AGUIToolCallFunction { Name = "get_weather", Arguments = "{}" } },
                },
            },
            new AGUIAssistantMessage
            {
                Id = "asst-2",
                Content = string.Empty,
                ToolCalls = new List<AGUIToolCall>
                {
                    new AGUIToolCall { Id = "tc_2", Type = "function", Function = new AGUIToolCallFunction { Name = "get_user_location", Arguments = "{}" } },
                },
            },
        };

        var chatMessages = aguiMessages.AsChatMessages().ToList();

        var assistantMessages = chatMessages.Where(m => m.Role == ChatRole.Assistant).ToList();
        Assert.Single(assistantMessages);
        Assert.Equal(2, assistantMessages[0].Contents.OfType<FunctionCallContent>().Count());
    }

    // Issue #2699 — the coalesced assistant message must be followed by the tool results, so a
    // full split parallel turn reconstructs as a valid provider history:
    //   assistant(tc_1, tc_2), tool(tc_1), tool(tc_2).
    [Fact]
    public void AsChatMessages_ParallelToolCalls_FollowedByResults_ProducesValidOrder()
    {
        var aguiMessages = new AGUIMessage[]
        {
            new AGUIAssistantMessage { Id = "asst-1", Content = string.Empty, ToolCalls = new List<AGUIToolCall> { new AGUIToolCall { Id = "tc_1", Type = "function", Function = new AGUIToolCallFunction { Name = "get_weather", Arguments = "{}" } } } },
            new AGUIAssistantMessage { Id = "asst-2", Content = string.Empty, ToolCalls = new List<AGUIToolCall> { new AGUIToolCall { Id = "tc_2", Type = "function", Function = new AGUIToolCallFunction { Name = "get_user_location", Arguments = "{}" } } } },
            new AGUIToolMessage { Id = "tool-1", ToolCallId = "tc_1", Content = "sunny" },
            new AGUIToolMessage { Id = "tool-2", ToolCallId = "tc_2", Content = "Paris" },
        };

        var chatMessages = aguiMessages.AsChatMessages().ToList();

        Assert.Equal(3, chatMessages.Count);
        Assert.Equal(ChatRole.Assistant, chatMessages[0].Role);
        var calls = chatMessages[0].Contents.OfType<FunctionCallContent>().Select(c => c.CallId).ToList();
        Assert.Equal(new[] { "tc_1", "tc_2" }, calls);
        Assert.Equal(ChatRole.Tool, chatMessages[1].Role);
        Assert.Equal("tc_1", chatMessages[1].Contents.OfType<FunctionResultContent>().Single().CallId);
        Assert.Equal(ChatRole.Tool, chatMessages[2].Role);
        Assert.Equal("tc_2", chatMessages[2].Contents.OfType<FunctionResultContent>().Single().CallId);
    }

    // Issue #2699 — legitimate sequential tool calls (each assistant call already followed by its
    // result) must NOT be merged; they are already valid.
    [Fact]
    public void AsChatMessages_SequentialToolCalls_NotMerged()
    {
        var aguiMessages = new AGUIMessage[]
        {
            new AGUIAssistantMessage { Id = "asst-1", Content = string.Empty, ToolCalls = new List<AGUIToolCall> { new AGUIToolCall { Id = "tc_1", Type = "function", Function = new AGUIToolCallFunction { Name = "step_a", Arguments = "{}" } } } },
            new AGUIToolMessage { Id = "tool-1", ToolCallId = "tc_1", Content = "a" },
            new AGUIAssistantMessage { Id = "asst-2", Content = string.Empty, ToolCalls = new List<AGUIToolCall> { new AGUIToolCall { Id = "tc_2", Type = "function", Function = new AGUIToolCallFunction { Name = "step_b", Arguments = "{}" } } } },
            new AGUIToolMessage { Id = "tool-2", ToolCallId = "tc_2", Content = "b" },
        };

        var chatMessages = aguiMessages.AsChatMessages().ToList();

        var assistantMessages = chatMessages.Where(m => m.Role == ChatRole.Assistant).ToList();
        Assert.Equal(2, assistantMessages.Count);
        Assert.All(assistantMessages, m => Assert.Single(m.Contents.OfType<FunctionCallContent>()));
    }
}
