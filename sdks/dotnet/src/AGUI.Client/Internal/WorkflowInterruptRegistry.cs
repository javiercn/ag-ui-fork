using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace AGUI.Client;

internal static class WorkflowInterruptRegistry
{
    internal const string InterruptKey = "agui.workflow.interrupt";
    internal const string ThreadIdKey = "agui.workflow.thread_id";
    internal const string CallIdKey = "agui.workflow.call_id";
    internal const string CallNameKey = "agui.workflow.call_name";
    internal const string CallArgumentsKey = "agui.workflow.call_arguments";
    internal const string ResumeStatusKey = "agui.resume.status";
    internal const string ResumeMetadataKey = "workflowInterrupt";
    internal const string PendingInterruptIdsMetadataKey = "pendingWorkflowInterruptIds";

    internal static void Attach(
        FunctionCallContent call,
        AGUIInterrupt interrupt,
        string threadId,
        JsonSerializerOptions jsonSerializerOptions)
    {
        call.AdditionalProperties ??= [];
        call.AdditionalProperties[InterruptKey] = JsonSerializer.SerializeToElement(
            interrupt,
            jsonSerializerOptions.GetTypeInfo(typeof(AGUIInterrupt)));
        call.AdditionalProperties[ThreadIdKey] = threadId;
        call.AdditionalProperties[CallIdKey] = call.CallId;
        call.AdditionalProperties[CallNameKey] = call.Name;
        call.AdditionalProperties[CallArgumentsKey] = JsonSerializer.SerializeToElement(
            call.Arguments,
            jsonSerializerOptions.GetTypeInfo(typeof(IDictionary<string, object?>)));
        call.RawRepresentation = interrupt;
        call.InformationalOnly = false;
    }

    internal static bool TryGet(
        FunctionCallContent call,
        out AGUIInterrupt? interrupt,
        out string? threadId)
    {
        interrupt = null;
        threadId = null;

        if (call.AdditionalProperties?.TryGetValue(InterruptKey, out object? value) is not true)
        {
            return false;
        }

        interrupt = value switch
        {
            AGUIInterrupt typed => typed,
            JsonElement element => (AGUIInterrupt?)element.Deserialize(
                AGUIJsonSerializerContext.Default.AGUIInterrupt),
            _ => throw new InvalidOperationException(
                $"Workflow interrupt metadata on call '{call.CallId}' is not serializable."),
        };

        if (interrupt is null)
        {
            throw new InvalidOperationException(
                $"Workflow interrupt metadata on call '{call.CallId}' is invalid.");
        }

        call.AdditionalProperties.TryGetValue(ThreadIdKey, out threadId);
        return true;
    }

    internal static bool MatchesOriginalCall(FunctionCallContent call)
    {
        if (call.AdditionalProperties?.TryGetValue(CallIdKey, out string? callId) is not true
            || call.AdditionalProperties.TryGetValue(CallNameKey, out string? callName) is not true
            || call.AdditionalProperties.TryGetValue(CallArgumentsKey, out JsonElement arguments) is not true)
        {
            return false;
        }

        var currentArguments = JsonSerializer.SerializeToElement(
            call.Arguments,
            AGUIJsonSerializerContext.Default.GetTypeInfo(typeof(IDictionary<string, object?>))!);
        return string.Equals(call.CallId, callId, StringComparison.Ordinal)
            && string.Equals(call.Name, callName, StringComparison.Ordinal)
            && JsonElement.DeepEquals(currentArguments, arguments);
    }
}
