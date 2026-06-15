using System.Text.Json.Serialization;

namespace Step06_RawEvents;

internal sealed class CounterState
{
    [JsonPropertyName("counter")]
    public int Counter { get; set; }

    [JsonPropertyName("lastAction")]
    public string LastAction { get; set; } = "none";
}
