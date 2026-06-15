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

namespace Step05_StateManagement;

internal static class AGUIEndpoint
{
    private const string RecipeInstructions = """
        You are a helpful recipe assistant. When users ask you to create or suggest a recipe,
        respond with a complete JSON object that includes:
        - recipe.title: The recipe name
        - recipe.cuisine: Type of cuisine (e.g., Italian, Mexican, Japanese)
        - recipe.ingredients: Array of ingredient strings with quantities
        - recipe.steps: Array of cooking instruction strings
        - recipe.prep_time_minutes: Preparation time in minutes
        - recipe.cook_time_minutes: Cooking time in minutes
        - recipe.skill_level: One of "beginner", "intermediate", or "advanced"

        Always include all fields in the response. Be creative and helpful.
        """;

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

            // Check if client sent state
            bool hasState = input.State is { ValueKind: JsonValueKind.Object } state &&
                HasProperties(state);

            IAsyncEnumerable<BaseEvent> events;
            if (hasState)
            {
                // State management mode: generate structured state + summary
                events = HandleStateManagementAsync(
                    chatClient, ctx, input, jsonSerializerOptions, cancellationToken);
            }
            else
            {
                // Simple pass-through mode
                events = chatClient.GetStreamingResponseAsync(ctx.Messages, ctx.ChatOptions, cancellationToken)
                    .AsAGUIEventStreamAsync(ctx, cancellationToken);
            }

            return TypedResults.ServerSentEvents(WrapAsSseItems(events, cancellationToken));
        });
    }

    private static async IAsyncEnumerable<BaseEvent> HandleStateManagementAsync(
        IChatClient chatClient,
        ChatRequestContext ctx,
        RunAgentInput input,
        JsonSerializerOptions jsonSerializerOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new RunStartedEvent
        {
            ThreadId = input.ThreadId,
            RunId = input.RunId
        };

        // First LLM call: generate structured state using JSON schema response format
        var stateOptions = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema<AgentState>(
                schemaName: "AgentState",
                schemaDescription: "A response containing a recipe with title, skill level, cooking time, ingredients, and instructions")
        };

        // Add current state context to messages
        var stateMessages = new List<ChatMessage>(ctx.Messages)
        {
            new(ChatRole.System, RecipeInstructions),
            new(ChatRole.System,
            [
                new TextContent("Here is the current state in JSON format:"),
                new TextContent(JsonSerializer.Serialize(
                    input.State!.Value,
                    jsonSerializerOptions.GetTypeInfo(typeof(JsonElement)))),
                new TextContent("The new state is:")
            ])
        };

        // Collect the structured response
        var structuredUpdates = new List<ChatResponseUpdate>();
        await foreach (var update in chatClient.GetStreamingResponseAsync(
            stateMessages, stateOptions, cancellationToken).ConfigureAwait(false))
        {
            structuredUpdates.Add(update);
        }

        // Try to parse as AgentState and emit StateSnapshotEvent
        var responseText = string.Concat(structuredUpdates
            .Where(u => !string.IsNullOrEmpty(u.Text))
            .Select(u => u.Text));

        AgentState? agentState = null;
        if (!string.IsNullOrEmpty(responseText))
        {
            agentState = (AgentState?)JsonSerializer.Deserialize(
                responseText,
                jsonSerializerOptions.GetTypeInfo(typeof(AgentState)));
        }

        if (agentState != null)
        {
            var stateSnapshot = JsonSerializer.SerializeToElement(
                agentState,
                jsonSerializerOptions.GetTypeInfo(typeof(AgentState)));

            yield return new StateSnapshotEvent
            {
                Snapshot = stateSnapshot
            };
        }

        // Second LLM call: generate user-friendly summary
        var summaryMessages = new List<ChatMessage>(ctx.Messages)
        {
            new(ChatRole.Assistant, responseText ?? ""),
            new(ChatRole.System, "Please provide a concise summary of the recipe in at most two sentences.")
        };

        await foreach (var evt in chatClient.GetStreamingResponseAsync(
                summaryMessages, ctx.ChatOptions, cancellationToken)
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

    private static bool HasProperties(JsonElement element)
    {
        foreach (JsonProperty _ in element.EnumerateObject())
        {
            return true;
        }

        return false;
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
