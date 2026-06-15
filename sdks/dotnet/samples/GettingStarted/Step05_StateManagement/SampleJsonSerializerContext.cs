using System.Text.Json.Serialization;

namespace Step05_StateManagement;

[JsonSerializable(typeof(AgentState))]
[JsonSerializable(typeof(RecipeState))]
internal sealed partial class SampleJsonSerializerContext : JsonSerializerContext;
