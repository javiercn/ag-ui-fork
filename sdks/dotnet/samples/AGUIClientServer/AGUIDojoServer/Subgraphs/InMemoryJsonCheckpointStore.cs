using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace AGUIDojoServer.Subgraphs;

internal sealed class InMemoryJsonCheckpointStore : JsonCheckpointStore
{
    private readonly ConcurrentDictionary<(string RunId, string CheckpointId), JsonElement> _checkpoints = new();
    private readonly ConcurrentDictionary<string, HashSet<CheckpointInfo>> _index = new();

    public override ValueTask<CheckpointInfo> CreateCheckpointAsync(string runId, JsonElement value, CheckpointInfo? parent = null)
    {
        var key = new CheckpointInfo(runId, Guid.NewGuid().ToString("N"));
        _checkpoints[(runId, key.CheckpointId)] = value;

        var runIndex = _index.GetOrAdd(runId, _ => []);
        lock (runIndex)
        {
            runIndex.Add(key);
        }

        return ValueTask.FromResult(key);
    }

    public override ValueTask<JsonElement> RetrieveCheckpointAsync(string runId, CheckpointInfo key)
    {
        if (_checkpoints.TryGetValue((runId, key.CheckpointId), out var checkpoint))
        {
            return ValueTask.FromResult(checkpoint);
        }

        throw new KeyNotFoundException($"Checkpoint '{key.CheckpointId}' not found for runId '{runId}'.");
    }

    public override ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string runId, CheckpointInfo? withParent = null)
    {
        if (_index.TryGetValue(runId, out var runIndex))
        {
            lock (runIndex)
            {
                var result = runIndex.ToList();
                return ValueTask.FromResult<IEnumerable<CheckpointInfo>>(result);
            }
        }

        return ValueTask.FromResult<IEnumerable<CheckpointInfo>>([]);
    }
}
