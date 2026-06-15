using System.Collections.Concurrent;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;

namespace AGUIWithCopilotSdkServer;

/// <summary>
/// Maps AG-UI <c>ThreadId</c>s to GitHub Copilot CLI <see cref="CopilotSession"/>s
/// so the stateful CLI runtime can be exposed through the stateless AG-UI HTTP
/// contract.
///
/// The registry deliberately holds no per-thread bridge state beyond the
/// session reference itself. All "what has Copilot seen?" reasoning is
/// delegated to <see cref="CopilotHistoryDiff"/>, which calls
/// <see cref="CopilotSession.GetEventsAsync"/> on demand. That makes the
/// bridge:
///
/// <list type="bullet">
///   <item><b>Durable across process restarts</b> — Copilot persists session
///         events to disk under <c>~/.copilot</c>; resuming the session
///         restores them and the diff still finds the right suffix.</item>
///   <item><b>Free of in-memory cache divergence</b> — there's no separate
///         "projection list" to keep in sync with the session; the session
///         <em>is</em> the projection.</item>
///   <item><b>Free of third-party storage</b> — no external database,
///         distributed cache, or sidecar process is needed; AG-UI
///         <c>ThreadId</c> + Copilot's own on-disk state is the entire bridge.</item>
/// </list>
/// </summary>
public sealed class CopilotSessionRegistry : IAsyncDisposable
{
    private readonly CopilotClient _client;
    private readonly ILogger<CopilotSessionRegistry> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<CopilotSession>>> _sessions = new(StringComparer.Ordinal);
    private readonly string? _model;

    public CopilotSessionRegistry(CopilotClient client, ILogger<CopilotSessionRegistry> logger, string? model = null)
    {
        _client = client;
        _logger = logger;
        _model = model;
    }

    public Task<CopilotSession> GetOrCreateAsync(string threadId, CancellationToken cancellationToken)
    {
        Lazy<Task<CopilotSession>> entry = _sessions.GetOrAdd(threadId, id => new Lazy<Task<CopilotSession>>(
            () => CreateOrResumeAsync(id, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication));
        return entry.Value;
    }

    private async Task<CopilotSession> CreateOrResumeAsync(string threadId, CancellationToken cancellationToken)
    {
        try
        {
            CopilotSession resumed = await _client.ResumeSessionAsync(
                threadId,
                new ResumeSessionConfig
                {
                    OnPermissionRequest = PermissionHandler.ApproveAll,
                },
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Resumed Copilot session {ThreadId}", threadId);
            return resumed;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Resume failed for {ThreadId}; creating a fresh session", threadId);
        }

        SessionConfig config = new()
        {
            SessionId = threadId,
            Streaming = true,
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };
        if (!string.IsNullOrEmpty(_model))
        {
            config.Model = _model;
        }

        CopilotSession created = await _client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Created Copilot session {ThreadId}", threadId);
        return created;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (Lazy<Task<CopilotSession>> entry in _sessions.Values)
        {
            if (!entry.IsValueCreated)
            {
                continue;
            }
            try
            {
                CopilotSession session = await entry.Value.ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing Copilot session");
            }
        }
        _sessions.Clear();
    }
}
