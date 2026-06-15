using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace AGUIWithMcpToolsServer;

/// <summary>
/// Wraps the <see cref="McpClient"/> and caches its tool list so each AG-UI
/// request doesn't pay a round-trip to <c>ListToolsAsync</c>. The cache is
/// populated lazily on the first call and refreshed only if the underlying
/// connection emits a tool-list-changed notification (registering that
/// handler is left as an exercise for production code).
/// </summary>
public sealed class McpToolProvider
{
    private readonly McpClient _client;
    private IReadOnlyList<AITool>? _cache;
    private readonly SemaphoreSlim _refresh = new(1, 1);

    public McpToolProvider(McpClient client) => _client = client;

    public async ValueTask<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is null)
            {
                IList<McpClientTool> mcpTools = await _client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                // McpClientTool : AIFunction, so the cast is safe and means the
                // ChatClient can call the tool by name like any other AIFunction.
                _cache = mcpTools.Cast<AITool>().ToList();
            }
        }
        finally
        {
            _refresh.Release();
        }

        return _cache!;
    }
}
