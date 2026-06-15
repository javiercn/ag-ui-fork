using System.Text.Json.Serialization;

namespace Step04_HumanInLoop;

[JsonSerializable(typeof(ApprovalRequest))]
[JsonSerializable(typeof(ApprovalResponse))]
internal sealed partial class SampleJsonSerializerContext : JsonSerializerContext;
