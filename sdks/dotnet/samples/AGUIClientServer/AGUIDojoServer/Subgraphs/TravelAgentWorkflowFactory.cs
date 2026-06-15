using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace AGUIDojoServer.Subgraphs;

public static class TravelAgentWorkflowFactory
{
    private const string StateKey = "travelState";
    private const string StateScope = "Travel";

    public static Workflow Create()
    {
        var supervisor = new SupervisorExecutor();
        var flightAgent = new FlightsExecutor();
        var flightRequest = RequestPort.Create<FlightSelectionRequest, Flight>("SelectFlight");
        var hotelAgent = new HotelsExecutor();
        var hotelRequest = RequestPort.Create<HotelSelectionRequest, Hotel>("SelectHotel");
        var experiencesAgent = new ExperiencesExecutor();

        var builder = new WorkflowBuilder(supervisor);

        builder.AddEdge<Flight>(
            supervisor,
            supervisor,
            flight => flight is not null);
        builder.AddEdge<Hotel>(
            supervisor,
            supervisor,
            hotel => hotel is not null);

        builder.AddEdge(supervisor, flightAgent);
        builder.AddEdge(flightAgent, flightRequest);
        builder.AddEdge(flightRequest, supervisor);

        builder.AddEdge(supervisor, hotelAgent);
        builder.AddEdge(hotelAgent, hotelRequest);
        builder.AddEdge(hotelRequest, supervisor);

        builder.AddEdge(supervisor, experiencesAgent);
        builder.AddEdge(experiencesAgent, supervisor);

        return builder.Build();
    }

    internal static async ValueTask<TravelAgentState> GetStateAsync(IWorkflowContext context, CancellationToken cancellationToken)
    {
        return await context.ReadOrInitStateAsync(StateKey, () => new TravelAgentState(), StateScope, cancellationToken);
    }

    internal static async ValueTask UpdateStateAsync(IWorkflowContext context, TravelAgentState state, CancellationToken cancellationToken)
    {
        await context.QueueStateUpdateAsync(StateKey, state, StateScope, cancellationToken);
    }

    private static readonly JsonSerializerOptions s_stateJsonOptions = new(JsonSerializerDefaults.Web);

    internal static async ValueTask EmitStateSnapshotAsync(
        IWorkflowContext context,
        TravelAgentState state,
        string activeAgent,
        CancellationToken cancellationToken)
    {
        var aguiState = AGUIStateSnapshot.FromTravelAgentState(state, activeAgent);
        var jsonString = JsonSerializer.Serialize(aguiState, s_stateJsonOptions);
        var jsonElement = JsonDocument.Parse(jsonString).RootElement.Clone();

        var stateSnapshotEvent = new StateSnapshotEvent
        {
            Snapshot = jsonElement
        };

        await context.AddEventAsync(
            new AgentResponseUpdateEvent(
                "StateSnapshot",
                new AgentResponseUpdate { RawRepresentation = stateSnapshotEvent }),
            cancellationToken);
    }
}

internal sealed class SupervisorExecutor() : ChatProtocolExecutor("Supervisor")
{
    protected override Microsoft.Agents.AI.Workflows.ProtocolBuilder ConfigureProtocol(Microsoft.Agents.AI.Workflows.ProtocolBuilder protocolBuilder)
    {
        return base.ConfigureProtocol(protocolBuilder)
            .ConfigureRoutes(routeBuilder => routeBuilder
                .AddHandler<Flight>(this.HandleSelectedFlightAsync)
                .AddHandler<Hotel>(this.HandleSelectedHotelAsync));
    }

    private async ValueTask HandleSelectedFlightAsync(Flight flight, IWorkflowContext context, CancellationToken cancellationToken)
    {
        var state = await TravelAgentWorkflowFactory.GetStateAsync(context, cancellationToken);
        state.Itinerary.SelectedFlight = flight;
        await TravelAgentWorkflowFactory.UpdateStateAsync(context, state, cancellationToken);

        await TravelAgentWorkflowFactory.EmitStateSnapshotAsync(context, state, "supervisor", cancellationToken);

        await context.AddEventAsync(new AgentResponseUpdateEvent(
            this.Id,
            new AgentResponseUpdate(ChatRole.Assistant, $"Flights Agent: Great! I'll book you the {flight.Airline} flight from {flight.Departure} to {flight.Arrival}.")),
            cancellationToken);

        await context.SendMessageAsync(state, "HotelsExecutor", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask HandleSelectedHotelAsync(Hotel hotel, IWorkflowContext context, CancellationToken cancellationToken)
    {
        var state = await TravelAgentWorkflowFactory.GetStateAsync(context, cancellationToken);
        state.Itinerary.SelectedHotel = hotel;
        await TravelAgentWorkflowFactory.UpdateStateAsync(context, state, cancellationToken);

        await TravelAgentWorkflowFactory.EmitStateSnapshotAsync(context, state, "supervisor", cancellationToken);

        await context.AddEventAsync(new AgentResponseUpdateEvent(
            this.Id,
            new AgentResponseUpdate(ChatRole.Assistant, $"Hotels Agent: Excellent choice! You'll love {hotel.Name} in {hotel.Location}.")),
            cancellationToken);

        await context.SendMessageAsync(state, "ExperiencesExecutor", cancellationToken).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    protected override async ValueTask TakeTurnAsync(
        List<ChatMessage> messages,
        IWorkflowContext context,
        bool? emitEvents,
        CancellationToken cancellationToken = default)
    {
        // Check for interrupt responses from the adapter
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent frc && frc.RawRepresentation is ExternalRequest externalRequest)
                {
                    var responseType = externalRequest.PortInfo.ResponseType;
                    object? typedResult = null;

                    if (frc.Result is JsonElement jsonElement)
                    {
                        var typeString = responseType.TypeName + ", " + responseType.AssemblyName;
                        var targetType = Type.GetType(typeString);

                        if (targetType == null)
                        {
                            targetType = Type.GetType(responseType.TypeName);
                        }

                        typedResult = JsonSerializer.Deserialize(
                            jsonElement.GetRawText(),
                            targetType ?? typeof(object),
                            s_jsonOptions);
                    }
                    else
                    {
                        typedResult = frc.Result;
                    }

                    switch (typedResult)
                    {
                        case Flight flight:
                            await HandleSelectedFlightAsync(flight, context, cancellationToken);
                            return;
                        case Hotel hotel:
                            await HandleSelectedHotelAsync(hotel, context, cancellationToken);
                            return;
                    }
                }
            }
        }

        // No pending results - proceed with normal routing based on state
        var state = await TravelAgentWorkflowFactory.GetStateAsync(context, cancellationToken);

        var hasInitialized = !string.IsNullOrEmpty(state.Origin) && !string.IsNullOrEmpty(state.Destination);
        if (!hasInitialized)
        {
            await context.AddEventAsync(new AgentResponseUpdateEvent(
                this.Id,
                new AgentResponseUpdate(ChatRole.Assistant, $"Noted your travel from {state.Origin} to {state.Destination}. Let me find some flight options for you!")),
                cancellationToken);
        }

        if (state.Itinerary.SelectedFlight is null)
        {
            await context.SendMessageAsync(state, "FlightsExecutor", cancellationToken);
        }
        else if (state.Itinerary.SelectedHotel is null)
        {
            await context.SendMessageAsync(state, "HotelsExecutor", cancellationToken);
        }
        else if (state.Itinerary.SelectedExperiences is null)
        {
            await context.SendMessageAsync(state, "ExperiencesExecutor", cancellationToken);
        }
        else
        {
            var summary = $"""
                🎉 Your trip to {state.Destination} is all planned!

                ✈️ Flight: {state.Itinerary.SelectedFlight.Airline} - {state.Itinerary.SelectedFlight.Price}
                   {state.Itinerary.SelectedFlight.Departure} → {state.Itinerary.SelectedFlight.Arrival}

                🏨 Hotel: {state.Itinerary.SelectedHotel.Name}
                   {state.Itinerary.SelectedHotel.Location} - {state.Itinerary.SelectedHotel.PricePerNight}

                🎯 Experiences: {state.Itinerary.SelectedExperiences.Count} activities planned

                Have an amazing trip!
                """;

            await context.AddEventAsync(new AgentResponseUpdateEvent(
                this.Id,
                new AgentResponseUpdate(ChatRole.Assistant, summary)),
                cancellationToken);
        }
    }
}

internal sealed class FlightsExecutor() : Executor<TravelAgentState>("FlightsExecutor")
{
    public override async ValueTask HandleAsync(TravelAgentState message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var flights = TravelData.Flights;

        var state = await TravelAgentWorkflowFactory.GetStateAsync(context, cancellationToken);
        state.Flights = [.. flights];
        await TravelAgentWorkflowFactory.UpdateStateAsync(context, state, cancellationToken);

        await TravelAgentWorkflowFactory.EmitStateSnapshotAsync(context, state, "flights", cancellationToken);

        var request = new FlightSelectionRequest
        {
            Message = $"Found {flights.Length} flight options from {state.Origin} to {state.Destination}. I recommend the {flights[0].Airline} flight as it's cheaper and has good on-time performance.",
            Options = [.. flights],
            Recommendation = flights[0],
            Agent = "flights"
        };

        await context.SendMessageAsync(request, cancellationToken: cancellationToken);
    }
}

internal sealed class HotelsExecutor() : Executor<TravelAgentState>("HotelsExecutor")
{
    public override async ValueTask HandleAsync(TravelAgentState message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var hotels = TravelData.Hotels;

        var state = await TravelAgentWorkflowFactory.GetStateAsync(context, cancellationToken);
        state.Hotels = [.. hotels];
        await TravelAgentWorkflowFactory.UpdateStateAsync(context, state, cancellationToken);

        await TravelAgentWorkflowFactory.EmitStateSnapshotAsync(context, state, "hotels", cancellationToken);

        var request = new HotelSelectionRequest
        {
            Message = $"Found {hotels.Length} accommodation options in {state.Destination}. I recommend {hotels[2].Name} as it offers the best balance of price, rating, and location.",
            Options = [.. hotels],
            Recommendation = hotels[2],
            Agent = "hotels"
        };

        await context.SendMessageAsync(request, cancellationToken: cancellationToken);
    }
}

internal sealed class ExperiencesExecutor() : Executor<TravelAgentState>("ExperiencesExecutor")
{
    public override async ValueTask HandleAsync(TravelAgentState message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var experiences = TravelData.Experiences;

        var state = await TravelAgentWorkflowFactory.GetStateAsync(context, cancellationToken);
        state.Experiences = [.. experiences];
        state.Itinerary.SelectedExperiences = [.. experiences];
        await TravelAgentWorkflowFactory.UpdateStateAsync(context, state, cancellationToken);

        await TravelAgentWorkflowFactory.EmitStateSnapshotAsync(context, state, "experiences", cancellationToken);

        var activities = experiences.Where(e => e.Type == "activity").ToList();
        var restaurants = experiences.Where(e => e.Type == "restaurant").ToList();

        var experiencesMessage = $"Here are some great experiences for your trip to {state.Destination}:\n\n";
        experiencesMessage += "🎯 Activities:\n";
        foreach (var activity in activities)
        {
            experiencesMessage += $"  • {activity.Name} - {activity.Description} ({activity.Location})\n";
        }
        experiencesMessage += "\n🍽️ Restaurants:\n";
        foreach (var restaurant in restaurants)
        {
            experiencesMessage += $"  • {restaurant.Name} - {restaurant.Description} ({restaurant.Location})\n";
        }

        await context.AddEventAsync(new AgentResponseUpdateEvent(
            this.Id,
            new AgentResponseUpdate(ChatRole.Assistant, experiencesMessage)),
            cancellationToken);
    }
}
