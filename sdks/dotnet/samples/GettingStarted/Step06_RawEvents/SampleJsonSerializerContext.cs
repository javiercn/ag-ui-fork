using System.Text.Json.Serialization;

namespace Step06_RawEvents;

[JsonSerializable(typeof(CounterState))]
internal sealed partial class SampleJsonSerializerContext : JsonSerializerContext;
