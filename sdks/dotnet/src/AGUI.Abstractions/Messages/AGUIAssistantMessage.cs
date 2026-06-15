using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AGUI.Abstractions;

// Keep in sync with sdks/typescript/packages/core/src/types.ts
public sealed class AGUIAssistantMessage : AGUIMessage
{
    public override string Role => AGUIRoles.Assistant;

    [JsonPropertyName("toolCalls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<AGUIToolCall>? ToolCalls { get; set; }
}
