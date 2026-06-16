namespace AGUI.Client.Internal;

/// <summary>
/// Internal keys used by <see cref="AGUIChatClient"/> and its helpers to thread
/// information through <see cref="Microsoft.Extensions.AI.AdditionalPropertiesDictionary"/>
/// instances across pipeline boundaries. These are not part of the public surface;
/// callers should drive the wire format through <c>ChatOptions.RawRepresentationFactory</c>
/// (returning a <see cref="AGUI.Abstractions.RunAgentInput"/>) instead.
/// </summary>
internal static class AGUIClientInternalKeys
{
    /// <summary>
    /// Carries the AG-UI thread id when <see cref="Microsoft.Extensions.AI.ChatOptions.ConversationId"/>
    /// must be cleared so <c>FunctionInvokingChatClient</c> sends the full message history on each turn.
    /// </summary>
    internal const string ThreadId = "agui_thread_id";

    /// <summary>
    /// Carries a list of <see cref="Microsoft.Extensions.AI.ToolApprovalResponseContent"/> items
    /// from the outer chat client to <c>BuildRunAgentInput</c> so they can be encoded as
    /// <see cref="AGUI.Abstractions.AGUIResume"/> entries.
    /// </summary>
    internal const string ApprovalResponses = "agui_approval_responses";

    /// <summary>
    /// Carries a list of <see cref="AGUI.Abstractions.InterruptResponseContent"/> items
    /// from the outer chat client to <c>BuildRunAgentInput</c> so they can be encoded as
    /// <see cref="AGUI.Abstractions.AGUIResume"/> entries.
    /// </summary>
    internal const string InterruptResponses = "agui_interrupt_responses";
}
