namespace AGUI.Abstractions;

/// <summary>
/// Represents a reasoning message carrying the model's internal reasoning text.
/// </summary>
// Keep in sync with sdks/typescript/packages/core/src/types.ts
public sealed class AGUIReasoningMessage : AGUIMessage
{
    public override string Role => AGUIRoles.Reasoning;
}
