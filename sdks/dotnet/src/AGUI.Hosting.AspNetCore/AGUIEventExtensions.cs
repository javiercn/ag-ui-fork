using System.Text.Json;
using AGUI.Abstractions;

namespace AGUI.Hosting.AspNetCore;

internal static class RunStartedEventExtensions
{
    extension(RunStartedEvent)
    {
        public static RunStartedEvent Create(string threadId, string runId, string? parentRunId = null) =>
            new() { ThreadId = threadId, RunId = runId, ParentRunId = parentRunId };
    }
}

internal static class RunFinishedEventExtensions
{
    extension(RunFinishedEvent)
    {
        public static RunFinishedEvent Create(
            string threadId,
            string runId,
            RunFinishedOutcome? outcome = null) =>
            new() { ThreadId = threadId, RunId = runId, Outcome = outcome };
    }
}

internal static class TextMessageStartEventExtensions
{
    extension(TextMessageStartEvent)
    {
        public static TextMessageStartEvent Create(
            string messageId,
            string role,
            string? name = null,
            JsonElement? rawEvent = null) =>
            new() { MessageId = messageId, Role = role, Name = name, RawEvent = rawEvent };
    }
}

internal static class TextMessageEndEventExtensions
{
    extension(TextMessageEndEvent)
    {
        public static TextMessageEndEvent Create(string messageId, JsonElement? rawEvent = null) =>
            new() { MessageId = messageId, RawEvent = rawEvent };
    }
}

internal static class TextMessageContentEventExtensions
{
    extension(TextMessageContentEvent)
    {
        public static TextMessageContentEvent Create(
            string messageId,
            string delta,
            JsonElement? rawEvent = null) =>
            new() { MessageId = messageId, Delta = delta, RawEvent = rawEvent };
    }
}

internal static class ToolCallStartEventExtensions
{
    extension(ToolCallStartEvent)
    {
        public static ToolCallStartEvent Create(
            string toolCallId,
            string toolCallName,
            string? parentMessageId = null,
            JsonElement? rawEvent = null) =>
            new()
            {
                ToolCallId = toolCallId,
                ToolCallName = toolCallName,
                ParentMessageId = parentMessageId,
                RawEvent = rawEvent
            };
    }
}

internal static class ToolCallArgsEventExtensions
{
    extension(ToolCallArgsEvent)
    {
        public static ToolCallArgsEvent Create(
            string toolCallId,
            string delta,
            JsonElement? rawEvent = null) =>
            new() { ToolCallId = toolCallId, Delta = delta, RawEvent = rawEvent };
    }
}

internal static class ToolCallEndEventExtensions
{
    extension(ToolCallEndEvent)
    {
        public static ToolCallEndEvent Create(string toolCallId, JsonElement? rawEvent = null) =>
            new() { ToolCallId = toolCallId, RawEvent = rawEvent };
    }
}

internal static class ToolCallResultEventExtensions
{
    extension(ToolCallResultEvent)
    {
        public static ToolCallResultEvent Create(
            string toolCallId,
            string result,
            JsonElement? rawEvent = null) =>
            new() { ToolCallId = toolCallId, MessageId = toolCallId, Content = result, RawEvent = rawEvent };
    }
}
