using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Hosting.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Step06_RawEvents;

internal static class AGUIEndpoint
{
    internal static IEndpointConventionBuilder MapAGUI(
        this IEndpointRouteBuilder endpoints,
        string pattern)
    {
        return endpoints.MapPost(pattern, (
            [FromBody] RunAgentInput input,
            [FromServices] IChatClient chatClient,
            [FromServices] IOptions<JsonOptions> jsonOptions,
            CancellationToken cancellationToken) =>
        {
            var jsonSerializerOptions = jsonOptions.Value.SerializerOptions;

            var ctx = input.ToChatRequestContext(jsonSerializerOptions);

            var events = HandleCounterAsync(
                chatClient, ctx, input, jsonSerializerOptions, cancellationToken);

            return TypedResults.ServerSentEvents(WrapAsSseItems(events, cancellationToken));
        });
    }

    private static async IAsyncEnumerable<BaseEvent> HandleCounterAsync(
        IChatClient chatClient,
        ChatRequestContext ctx,
        RunAgentInput input,
        JsonSerializerOptions jsonSerializerOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Extract counter state from input
        CounterState currentState = GetStateFromInput(input);

        // Check the user message for counter commands
        string? userMessage = ctx.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;

        bool shouldModifyState = false;
        if (!string.IsNullOrEmpty(userMessage))
        {
            string upperMessage = userMessage.ToUpperInvariant();
            if (upperMessage.Contains("INCREMENT", StringComparison.Ordinal) ||
                upperMessage.Contains("ADD", StringComparison.Ordinal) ||
                upperMessage.Contains('+'))
            {
                currentState.Counter++;
                currentState.LastAction = "incremented";
                shouldModifyState = true;
            }
            else if (upperMessage.Contains("DECREMENT", StringComparison.Ordinal) ||
                upperMessage.Contains("SUBTRACT", StringComparison.Ordinal) ||
                upperMessage.Contains('-'))
            {
                currentState.Counter--;
                currentState.LastAction = "decremented";
                shouldModifyState = true;
            }
            else if (upperMessage.Contains("RESET", StringComparison.Ordinal) ||
                upperMessage.Contains("ZERO", StringComparison.Ordinal))
            {
                currentState.Counter = 0;
                currentState.LastAction = "reset";
                shouldModifyState = true;
            }
        }

        yield return new RunStartedEvent
        {
            ThreadId = input.ThreadId,
            RunId = input.RunId
        };

        // Emit StateSnapshotEvent if the counter was modified
        if (shouldModifyState)
        {
            var stateJson = JsonSerializer.SerializeToElement(
                currentState,
                jsonSerializerOptions.GetTypeInfo(typeof(CounterState)));

            yield return new StateSnapshotEvent
            {
                Snapshot = stateJson
            };
        }

        // Call LLM for text response
        await foreach (var evt in chatClient.GetStreamingResponseAsync(
                ctx.Messages, ctx.ChatOptions, cancellationToken)
            .AsAGUIEventStreamAsync(ctx, cancellationToken).ConfigureAwait(false))
        {
            // Skip RunStarted/RunFinished since we handle those ourselves
            if (evt is RunStartedEvent or RunFinishedEvent)
            {
                continue;
            }

            yield return evt;
        }

        yield return new RunFinishedEvent
        {
            ThreadId = input.ThreadId,
            RunId = input.RunId
        };
    }

    private static CounterState GetStateFromInput(RunAgentInput input)
    {
        if (input.State is { ValueKind: JsonValueKind.Object } state &&
            state.TryGetProperty("counter", out JsonElement counterElement) &&
            counterElement.ValueKind == JsonValueKind.Number)
        {
            return new CounterState
            {
                Counter = counterElement.GetInt32(),
                LastAction = state.TryGetProperty("lastAction", out JsonElement actionElement)
                    ? actionElement.GetString() ?? "none"
                    : "none"
            };
        }

        return new CounterState();
    }

    private static async IAsyncEnumerable<SseItem<BaseEvent>> WrapAsSseItems(
        IAsyncEnumerable<BaseEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new SseItem<BaseEvent>(evt);
        }
    }
}
