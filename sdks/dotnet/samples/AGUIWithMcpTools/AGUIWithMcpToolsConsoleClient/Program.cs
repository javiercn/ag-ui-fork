using AGUI.Client;
using Microsoft.Extensions.AI;

// Default to localhost:5018 matching the AGUIWithMcpToolsServer launchSettings.
// Pass a different URL as the first arg to point at a server bound elsewhere.
string baseUrl = args.Length > 0 ? args[0] : "http://localhost:5018";

Console.WriteLine("AGUIWithMcpToolsServer Console Client");
Console.WriteLine($"Server: {baseUrl}");
Console.WriteLine();

using HttpClient httpClient = new();
AGUIChatClient client = new(httpClient, $"{baseUrl}/");

// The server's FakeChatClient inspects the prompt and dispatches to one of
// the MCP-hosted tools (kb_search or kb_list). These prompts cover both
// dispatch branches so a quick run exercises the full pipeline:
//   AGUIChatClient -> AG-UI HTTP -> server IChatClient -> McpClient -> McpServer.
(string prompt, string label)[] scenarios =
[
    ("Use the kb_search tool to look up the article with id 'mcp' and quote its content verbatim.",   "kb_search → 'mcp' article"),
    ("Use the kb_search tool to look up the article with id 'agui' and quote its content verbatim.",  "kb_search → 'agui' article"),
    ("What articles do you have available?", "kb_list → enumerates IDs"),
];

foreach ((string prompt, string label) in scenarios)
{
    Console.WriteLine(new string('=', 60));
    Console.WriteLine($"SCENARIO: {label}");
    Console.WriteLine($"USER:     {prompt}");
    Console.WriteLine(new string('=', 60));

    List<ChatMessage> messages = [new(ChatRole.User, prompt)];

    Console.Write("ASSISTANT: ");
    try
    {
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages).ConfigureAwait(false))
        {
            foreach (AIContent content in update.Contents)
            {
                if (content is TextContent text)
                {
                    Console.Write(text.Text);
                }
                else if (content is FunctionCallContent call)
                {
                    Console.Write($"\n  [Tool Call: {call.Name}]");
                }
                else if (content is FunctionResultContent result)
                {
                    Console.Write($"\n  [Tool Result: {Truncate(result.Result?.ToString(), 200)}]");
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] {ex.GetType().Name}: {ex.Message}");
    }

    Console.WriteLine();
    Console.WriteLine();
}

Console.WriteLine("All scenarios completed.");

static string Truncate(string? value, int max) =>
    value is null ? "" :
    value.Length <= max ? value :
    value[..max] + "...";
