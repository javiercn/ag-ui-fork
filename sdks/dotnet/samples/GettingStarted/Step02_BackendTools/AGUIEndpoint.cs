using System.ComponentModel;
using System.Net.ServerSentEvents;
using AGUI.Abstractions;
using AGUI.Hosting.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Step02_BackendTools;

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

            // Add the server tool alongside any approval-wrapped client tools
            // already installed by ToChatRequestContext.
            ctx.ChatOptions.Tools ??= [];
            ctx.ChatOptions.Tools.Add(
                AIFunctionFactory.Create(
                    SearchRestaurants,
                    serializerOptions: jsonSerializerOptions));

            var updates = chatClient.GetStreamingResponseAsync(ctx.Messages, ctx.ChatOptions, cancellationToken);

            var events = updates.AsAGUIEventStreamAsync(ctx, cancellationToken);

            return TypedResults.ServerSentEvents(WrapAsSseItems(events, cancellationToken));
        });
    }

    [Description("Search for restaurants in a location.")]
    private static RestaurantSearchResponse SearchRestaurants(
        [Description("The restaurant search request")] RestaurantSearchRequest request)
    {
        string cuisine = request.Cuisine == "any" ? "Italian" : request.Cuisine;

        return new RestaurantSearchResponse
        {
            Location = request.Location,
            Cuisine = request.Cuisine,
            Results =
            [
                new RestaurantInfo
                {
                    Name = "The Golden Fork",
                    Cuisine = cuisine,
                    Rating = 4.5,
                    Address = $"123 Main St, {request.Location}"
                },
                new RestaurantInfo
                {
                    Name = "Spice Haven",
                    Cuisine = cuisine == "Italian" ? "Indian" : cuisine,
                    Rating = 4.7,
                    Address = $"456 Oak Ave, {request.Location}"
                },
                new RestaurantInfo
                {
                    Name = "Green Leaf",
                    Cuisine = "Vegetarian",
                    Rating = 4.3,
                    Address = $"789 Elm Rd, {request.Location}"
                }
            ]
        };
    }

    private static async IAsyncEnumerable<SseItem<BaseEvent>> WrapAsSseItems(
        IAsyncEnumerable<BaseEvent> events,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new SseItem<BaseEvent>(evt);
        }
    }
}


