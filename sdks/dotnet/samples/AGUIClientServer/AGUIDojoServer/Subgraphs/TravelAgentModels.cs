using System.Text.Json.Serialization;

namespace AGUIDojoServer.Subgraphs;

public enum RoutingDecision
{
    ToFlights,
    ToHotels,
    ToExperiences,
    Complete
}

public sealed class SupervisorResponse
{
    [JsonPropertyName("answer")]
    public string Answer { get; set; } = string.Empty;

    [JsonPropertyName("next_agent")]
    public string? NextAgent { get; set; }
}

public sealed class Flight
{
    public string Airline { get; set; } = string.Empty;
    public string Departure { get; set; } = string.Empty;
    public string Arrival { get; set; } = string.Empty;
    public string Price { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
}

public sealed class Hotel
{
    public string Name { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string PricePerNight { get; set; } = string.Empty;
    public string Rating { get; set; } = string.Empty;
}

public sealed class Experience
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
}

public sealed class FlightSelectionRequest
{
    public string Message { get; set; } = string.Empty;
    public List<Flight> Options { get; set; } = [];
    public Flight? Recommendation { get; set; }
    public string Agent { get; set; } = "flights";
}

public sealed class HotelSelectionRequest
{
    public string Message { get; set; } = string.Empty;
    public List<Hotel> Options { get; set; } = [];
    public Hotel? Recommendation { get; set; }
    public string Agent { get; set; } = "hotels";
}

public sealed class TravelItinerary
{
    public Flight? SelectedFlight { get; set; }
    public Hotel? SelectedHotel { get; set; }
    public List<Experience>? SelectedExperiences { get; set; }
}

public sealed class TravelAgentState
{
    public string Origin { get; set; } = "Amsterdam";
    public string Destination { get; set; } = "San Francisco";
    public List<Flight>? Flights { get; set; }
    public List<Hotel>? Hotels { get; set; }
    public List<Experience>? Experiences { get; set; }
    public TravelItinerary Itinerary { get; set; } = new();
}

public sealed class AGUIStateSnapshot
{
    [JsonPropertyName("flights")]
    public List<AGUIFlight> Flights { get; set; } = [];

    [JsonPropertyName("hotels")]
    public List<AGUIHotel> Hotels { get; set; } = [];

    [JsonPropertyName("experiences")]
    public List<AGUIExperience> Experiences { get; set; } = [];

    [JsonPropertyName("itinerary")]
    public AGUIItinerary Itinerary { get; set; } = new();

    [JsonPropertyName("planning_step")]
    public string PlanningStep { get; set; } = "start";

    [JsonPropertyName("active_agent")]
    public string ActiveAgent { get; set; } = "supervisor";

    public static AGUIStateSnapshot FromTravelAgentState(TravelAgentState state, string activeAgent = "supervisor")
    {
        return new AGUIStateSnapshot
        {
            Flights = state.Flights?.Select(f => new AGUIFlight
            {
                Airline = f.Airline,
                Departure = f.Departure,
                Arrival = f.Arrival,
                Price = f.Price,
                Duration = f.Duration
            }).ToList() ?? [],
            Hotels = state.Hotels?.Select(h => new AGUIHotel
            {
                Name = h.Name,
                Location = h.Location,
                PricePerNight = h.PricePerNight,
                Rating = h.Rating
            }).ToList() ?? [],
            Experiences = state.Experiences?.Select(e => new AGUIExperience
            {
                Name = e.Name,
                Type = e.Type,
                Description = e.Description,
                Location = e.Location
            }).ToList() ?? [],
            Itinerary = new AGUIItinerary
            {
                Flight = state.Itinerary.SelectedFlight is { } f ? new AGUIFlight
                {
                    Airline = f.Airline,
                    Departure = f.Departure,
                    Arrival = f.Arrival,
                    Price = f.Price,
                    Duration = f.Duration
                } : null,
                Hotel = state.Itinerary.SelectedHotel is { } h ? new AGUIHotel
                {
                    Name = h.Name,
                    Location = h.Location,
                    PricePerNight = h.PricePerNight,
                    Rating = h.Rating
                } : null,
                Experiences = state.Itinerary.SelectedExperiences?.Select(e => new AGUIExperience
                {
                    Name = e.Name,
                    Type = e.Type,
                    Description = e.Description,
                    Location = e.Location
                }).ToList()
            },
            ActiveAgent = activeAgent,
            PlanningStep = state.Itinerary.SelectedFlight is not null
                ? (state.Itinerary.SelectedHotel is not null
                    ? (state.Itinerary.SelectedExperiences is not null ? "complete" : "experiences")
                    : "hotels")
                : "flights"
        };
    }
}

public sealed class AGUIFlight
{
    [JsonPropertyName("airline")]
    public string Airline { get; set; } = string.Empty;

    [JsonPropertyName("departure")]
    public string Departure { get; set; } = string.Empty;

    [JsonPropertyName("arrival")]
    public string Arrival { get; set; } = string.Empty;

    [JsonPropertyName("price")]
    public string Price { get; set; } = string.Empty;

    [JsonPropertyName("duration")]
    public string Duration { get; set; } = string.Empty;
}

public sealed class AGUIHotel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("price_per_night")]
    public string PricePerNight { get; set; } = string.Empty;

    [JsonPropertyName("rating")]
    public string Rating { get; set; } = string.Empty;
}

public sealed class AGUIExperience
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;
}

public sealed class AGUIItinerary
{
    [JsonPropertyName("flight")]
    public AGUIFlight? Flight { get; set; }

    [JsonPropertyName("hotel")]
    public AGUIHotel? Hotel { get; set; }

    [JsonPropertyName("experiences")]
    public List<AGUIExperience>? Experiences { get; set; }
}
