using System.Text.Json;
using System.Text.Json.Serialization;

namespace AGUI.Abstractions;

/// <summary>
/// Represents a generic interrupt request that pauses the agent run to request input
/// from the user or application. Carries structured fields from the AG-UI interrupt.
/// </summary>
// Keep in sync with sdks/typescript/packages/core/src/types.ts
public sealed class InterruptRequestContent : Microsoft.Extensions.AI.InputRequestContent
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InterruptRequestContent"/> class.
    /// </summary>
    /// <param name="requestId">The unique identifier that correlates this request with its corresponding response.</param>
    [JsonConstructor]
    public InterruptRequestContent(string requestId)
        : base(requestId)
    {
    }

    /// <summary>
    /// Gets or sets the interrupt reason (e.g. "tool_call", "input_required", "confirmation", or a custom value).
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets a human-readable message describing the interrupt.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the tool call identifier, when the interrupt is related to a tool call.
    /// </summary>
    public string? ToolCallId { get; set; }

    /// <summary>
    /// Gets or sets the JSON schema describing the expected response shape.
    /// </summary>
    public JsonElement? ResponseSchema { get; set; }

    /// <summary>
    /// Gets or sets the expiration time for the interrupt, as an ISO 8601 string.
    /// </summary>
    public string? ExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets additional metadata associated with the interrupt.
    /// </summary>
    public JsonElement? Metadata { get; set; }
}
