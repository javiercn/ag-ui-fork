using AGUI.Client;
using Microsoft.Extensions.AI;

// The Copilot bridge sample is fundamentally about *statefulness*: each AG-UI
// ThreadId maps 1:1 to a long-lived CopilotSession that owns its own
// conversation history. This console client drives a multi-turn conversation
// over a single ThreadId so the demo actually exercises that statefulness;
// running each prompt with a fresh ThreadId would defeat the point.

string baseUrl = args.Length > 0 ? args[0] : "http://localhost:5019";
string threadId = args.Length > 1 ? args[1] : $"thread-{DateTime.UtcNow:yyyyMMddHHmmss}";

Console.WriteLine("AGUIWithCopilotSdkServer Console Client");
Console.WriteLine($"Server:   {baseUrl}");
Console.WriteLine($"ThreadId: {threadId}");
Console.WriteLine();

using HttpClient httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
AGUIChatClient client = new(httpClient, $"{baseUrl}/agui");

// The three turns share a ConversationId so the server resolves the same
// CopilotSession on every request, and so AGUIChatClient sends the right
// ThreadId in RunAgentInput. Note that even though we keep a local
// `history` list for display, the server *ignores* prior turns — Copilot
// owns the conversation history server-side and only consumes the LAST
// user message on each call (see CopilotHistoryDiff in the sample).
ChatOptions options = new() { ConversationId = threadId };

string[] turns =
[
    "Hi! My name is Bob.",
    "What's the largest prime number under 50?",
    "What was my name again?",
];

List<ChatMessage> history = [];
foreach (string prompt in turns)
{
    Console.WriteLine(new string('=', 60));
    Console.WriteLine($"USER: {prompt}");
    Console.WriteLine(new string('=', 60));

    history.Add(new(ChatRole.User, prompt));

    Console.Write("ASSISTANT: ");
    try
    {
        // Stream while echoing to the console, but also collect the updates
        // so we can roll them into a properly-built ChatMessage via
        // ToChatResponseAsync. That preserves MessageId on the assistant
        // turn — the AG-UI client picks it up from the server's
        // TextMessageStartEvent (which carries Copilot's MessageId) and
        // propagates it through ChatResponseUpdate.MessageId per MEAI's
        // "ToChatResponseAsync may use MessageId to determine message
        // boundaries" contract. Re-sending that message on the next turn
        // lets the server's history-diff match by id instead of guessing
        // from content.
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(history, options).ConfigureAwait(false))
        {
            updates.Add(update);
            foreach (AIContent content in update.Contents)
            {
                if (content is TextContent text)
                {
                    Console.Write(text.Text);
                }
            }
        }

        ChatResponse response = updates.ToChatResponse();
        // Append every assistant message the response produced — usually
        // exactly one — with its MessageId intact so the next turn's
        // outbound RunAgentInput carries it as AGUIMessage.Id.
        foreach (ChatMessage assistantMessage in response.Messages)
        {
            history.Add(assistantMessage);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n[ERROR] {ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException is not null)
        {
            Console.WriteLine($"  Inner: {ex.InnerException.Message}");
        }
        Console.WriteLine("\nMake sure the AGUIWithCopilotSdkServer is running and that you've");
        Console.WriteLine("authenticated with `gh auth login` (or set GITHUB_TOKEN).");
        return;
    }
    Console.WriteLine();
    Console.WriteLine();
}

Console.WriteLine("All turns completed against the same Copilot session.");
Console.WriteLine($"Re-run with: dotnet run -- {baseUrl} {threadId}");
Console.WriteLine("to resume the same thread (the server will reuse the in-memory");
Console.WriteLine("session, or resume from disk on a fresh process).");
