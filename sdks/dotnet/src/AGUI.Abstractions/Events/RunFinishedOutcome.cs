using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AGUI.Abstractions;

[JsonConverter(typeof(RunFinishedOutcomeJsonConverter))]
// Keep in sync with sdks/typescript/packages/core/src/events.ts
public abstract class RunFinishedOutcome
{
    internal RunFinishedOutcome() { }

    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public sealed class RunFinishedSuccessOutcome : RunFinishedOutcome
{
    [JsonPropertyName("type")]
    public override string Type => "success";
}

public sealed class RunFinishedInterruptOutcome : RunFinishedOutcome
{
    [JsonPropertyName("type")]
    public override string Type => "interrupt";

    [JsonPropertyName("interrupts")]
    public IList<AGUIInterrupt> Interrupts { get; set; } = [];
}
