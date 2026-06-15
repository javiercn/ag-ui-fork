namespace AGUI.Abstractions;

// Keep in sync with sdks/typescript/packages/core/src/types.ts
public sealed class AGUISystemMessage : AGUIMessage
{
    public override string Role => AGUIRoles.System;
}
