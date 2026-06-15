using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;

namespace AGUIDojoServer.Subgraphs;

internal sealed class DebugAgentSessionStore : AgentSessionStore
{
    private readonly InMemoryAgentSessionStore _inner = new();

    public override async ValueTask SaveSessionAsync(AIAgent agent, string conversationId, AgentSession session, CancellationToken cancellationToken = default)
    {
        Console.WriteLine("[SESSION] SaveSessionAsync: conversationId=" + conversationId);
        await _inner.SaveSessionAsync(agent, conversationId, session, cancellationToken);
    }

    public override async ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
    {
        Console.WriteLine("[SESSION] GetSessionAsync: conversationId=" + conversationId);
        return await _inner.GetSessionAsync(agent, conversationId, cancellationToken);
    }
}
