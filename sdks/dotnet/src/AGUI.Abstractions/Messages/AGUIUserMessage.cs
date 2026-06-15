using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AGUI.Abstractions;

// Keep in sync with sdks/typescript/packages/core/src/types.ts
public sealed class AGUIUserMessage : AGUIMessage
{
    public override string Role => AGUIRoles.User;

    [JsonIgnore]
    public new IList<AGUIInputContent> Content { get; set; } = [];
}
