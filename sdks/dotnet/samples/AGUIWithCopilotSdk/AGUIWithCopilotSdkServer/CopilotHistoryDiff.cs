using System.Security.Cryptography;
using System.Text;
using AGUI.Abstractions;
using GitHub.Copilot;

namespace AGUIWithCopilotSdkServer;

/// <summary>
/// Computes the "new user prompts" delta between AG-UI's full-history request
/// payload and the Copilot session's own durable event log. This is the
/// stateless bridge between AG-UI (every turn re-sends the full history) and
/// Copilot (every turn expects only the new user message).
///
/// We use <see cref="CopilotSession.GetEventsAsync"/> as the source of truth.
/// It's persisted to disk by the Copilot CLI runtime, so projection survives
/// process restarts — no in-memory cache, no external storage, no
/// per-thread registry of "what we forwarded last time".
///
/// AG-UI messages don't always carry an Id (the .NET console client uses
/// <c>new ChatMessage(role, text)</c> which leaves <c>MessageId</c> null),
/// and Copilot's <c>UserMessageData</c> exposes <c>Content</c> + <c>InteractionId</c>
/// but no AG-UI-compatible identifier. We therefore key both sides on a
/// content hash of <c>(role + normalised text)</c> and walk the two
/// projections in order to find the longest matching prefix — anything in
/// the AG-UI history beyond that prefix is the new suffix to forward.
/// </summary>
internal static class CopilotHistoryDiff
{
    /// <summary>
    /// Returns the user prompts (in order) from <paramref name="incoming"/> that
    /// the Copilot session has not yet seen, and the index in <paramref name="incoming"/>
    /// where the projection stopped matching (useful for diagnostics / logging).
    /// </summary>
    public static async Task<(IReadOnlyList<AGUIUserMessage> NewUserMessages, int CommonPrefixLength)> ComputeNewUserMessagesAsync(
        CopilotSession session,
        IList<AGUIMessage> incoming,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SessionEvent> events = await session.GetEventsAsync(cancellationToken).ConfigureAwait(false);

        // Project both sides into (canonical hash) lists. Only user + assistant
        // turns count toward matching — system messages, tool calls, reasoning,
        // etc. live on one side or the other but aren't replayed in AG-UI's
        // ChatMessage history, so including them would force the prefix to 0.
        List<string> copilotProjection = ProjectCopilotEvents(events);
        List<string> aguiProjection = ProjectAGUIMessages(incoming);

        int prefix = 0;
        int max = Math.Min(copilotProjection.Count, aguiProjection.Count);
        while (prefix < max && copilotProjection[prefix] == aguiProjection[prefix])
        {
            prefix++;
        }

        // Walk the AG-UI tail (everything past the matched prefix) and pick out
        // only the USER messages — assistant entries in the tail came FROM
        // Copilot in a prior turn the client now resends, so re-forwarding
        // them would duplicate the conversation in Copilot's session.
        var newUserMessages = new List<AGUIUserMessage>();
        int aguiPrefix = MapHashPrefixToMessageIndex(incoming, aguiProjection, prefix);
        for (int i = aguiPrefix; i < incoming.Count; i++)
        {
            if (incoming[i] is AGUIUserMessage user)
            {
                newUserMessages.Add(user);
            }
        }

        return (newUserMessages, aguiPrefix);
    }

    private static List<string> ProjectCopilotEvents(IReadOnlyList<SessionEvent> events)
    {
        var projection = new List<string>(events.Count);
        foreach (SessionEvent evt in events)
        {
            switch (evt)
            {
                case UserMessageEvent user:
                    // Copilot's UserMessageData has no MessageId — only an
                    // InteractionId that's never echoed back to us. Hash the
                    // content so the AG-UI side (which usually also lacks
                    // an Id for user turns) can match by the same key.
                    projection.Add(HashKey("user", user.Data.Content));
                    break;
                case AssistantMessageEvent assistant when !string.IsNullOrEmpty(assistant.Data.Content):
                    // Assistant messages DO have a stable MessageId. We emit
                    // that as TextMessageStartEvent.MessageId; the AG-UI
                    // client preserves it through ChatResponseUpdate ->
                    // ChatMessage.MessageId (per MEAI's ToChatResponseAsync
                    // contract — "may use MessageId to determine message
                    // boundaries") and replays it back to us as
                    // AGUIMessage.Id on the next turn. Prefer that over a
                    // content hash so legitimate text drift (markdown
                    // normalisation, stripped trailing whitespace etc.)
                    // can't break matching.
                    projection.Add(!string.IsNullOrEmpty(assistant.Data.MessageId)
                        ? IdKey(assistant.Data.MessageId!)
                        : HashKey("assistant", assistant.Data.Content));
                    break;
            }
        }
        return projection;
    }

    private static List<string> ProjectAGUIMessages(IList<AGUIMessage> messages)
    {
        var projection = new List<string>(messages.Count);
        foreach (AGUIMessage message in messages)
        {
            switch (message)
            {
                case AGUIUserMessage user:
                    projection.Add(HashKey("user", FlattenUserContent(user)));
                    break;
                case AGUIAssistantMessage assistant when !string.IsNullOrEmpty(assistant.Content):
                    projection.Add(!string.IsNullOrEmpty(assistant.Id)
                        ? IdKey(assistant.Id)
                        : HashKey("assistant", assistant.Content));
                    break;
            }
        }
        return projection;
    }

    /// <summary>
    /// The hash-list projection skips messages that don't contribute to
    /// matching (e.g. system messages, tool calls), so its indices don't
    /// line up with the original AG-UI message list. Map a "hash prefix
    /// length" back to "AG-UI message index" so the caller can correctly
    /// slice the suffix.
    /// </summary>
    private static int MapHashPrefixToMessageIndex(
        IList<AGUIMessage> messages, IList<string> hashes, int hashPrefixLength)
    {
        if (hashPrefixLength == 0)
        {
            return 0;
        }

        int counted = 0;
        for (int i = 0; i < messages.Count; i++)
        {
            if (ContributesToProjection(messages[i]))
            {
                counted++;
                if (counted == hashPrefixLength)
                {
                    return i + 1;
                }
            }
        }
        // Defensive — the projection length should bound this.
        return messages.Count;
    }

    private static bool ContributesToProjection(AGUIMessage message) => message switch
    {
        AGUIUserMessage => true,
        AGUIAssistantMessage a => !string.IsNullOrEmpty(a.Content),
        _ => false,
    };

    public static string FlattenUserContent(AGUIUserMessage userMessage) =>
        string.Concat(userMessage.Content.Select(c => c switch
        {
            AGUITextInputContent t => t.Text,
            _ => $"[{c.GetType().Name}]",
        }));

    /// <summary>
    /// Wraps a known stable Id so it doesn't collide with content-hash keys.
    /// </summary>
    private static string IdKey(string id) => "id:" + id;

    /// <summary>
    /// Base64(SHA-256(role || NUL || normalised content)). Normalisation
    /// trims surrounding whitespace and collapses runs of whitespace inside
    /// the text so a tiny streaming delta artefact (e.g. a trailing newline)
    /// doesn't make Copilot's full-text version differ from the AG-UI client's
    /// re-concatenated deltas. Prefixed with <c>hash:</c> to keep it distinct
    /// from <see cref="IdKey"/> output.
    /// </summary>
    private static string HashKey(string role, string content)
    {
        string normalised = NormaliseContent(content);
        byte[] bytes = Encoding.UTF8.GetBytes($"{role}\0{normalised}");
        byte[] hash = SHA256.HashData(bytes);
        return "hash:" + Convert.ToBase64String(hash);
    }

    private static string NormaliseContent(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }
        ReadOnlySpan<char> span = content.AsSpan().Trim();
        StringBuilder sb = new(span.Length);
        bool lastWasWs = false;
        foreach (char c in span)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasWs)
                {
                    sb.Append(' ');
                    lastWasWs = true;
                }
            }
            else
            {
                sb.Append(c);
                lastWasWs = false;
            }
        }
        return sb.ToString();
    }
}
