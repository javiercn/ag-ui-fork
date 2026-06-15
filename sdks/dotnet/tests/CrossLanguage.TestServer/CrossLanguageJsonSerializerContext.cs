using System.Text.Json.Serialization;

namespace CrossLanguage.TestServer;

[JsonSerializable(typeof(WeatherReport))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal sealed partial class CrossLanguageJsonSerializerContext : JsonSerializerContext;
