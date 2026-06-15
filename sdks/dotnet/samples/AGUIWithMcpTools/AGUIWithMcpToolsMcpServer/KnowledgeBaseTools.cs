using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AGUIWithMcpToolsMcpServer;

/// <summary>
/// Tools exposed by the standalone stdio MCP server. The
/// <c>[McpServerToolType]</c>/<c>[McpServerTool]</c> attributes let
/// <c>AddMcpServer().WithToolsFromAssembly()</c> discover and register them
/// automatically when the host process starts.
///
/// These are deliberately small and deterministic — the goal of the
/// sample is to show the AG-UI ↔ ChatClient ↔ MCP wiring, not to
/// implement realistic knowledge-base tools.
/// </summary>
[McpServerToolType]
public static class KnowledgeBaseTools
{
    private static readonly IReadOnlyDictionary<string, string> s_articles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["agui"] = "AG-UI is a streaming HTTP protocol for delivering AI agent events.",
            ["mcp"] = "Model Context Protocol (MCP) standardises how LLMs talk to external tools.",
            ["copilot"] = "GitHub Copilot is an AI-powered coding assistant.",
        };

    [McpServerTool(Name = "kb_search")]
    [Description("Search the knowledge base for an article matching the query keyword.")]
    public static string Search(
        [Description("Keyword to look up (case-insensitive).")] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "No matches.";
        }

        string normalized = query.Trim();
        foreach (KeyValuePair<string, string> entry in s_articles)
        {
            if (normalized.Contains(entry.Key, StringComparison.OrdinalIgnoreCase))
            {
                return $"{entry.Key}: {entry.Value}";
            }
        }

        return "No matches.";
    }

    [McpServerTool(Name = "kb_list")]
    [Description("List the IDs of every article stored in the knowledge base.")]
    public static string List() => string.Join(", ", s_articles.Keys);
}
