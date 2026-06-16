using System.Text.Json;

namespace Step06_RawEvents.Server;

/// <summary>
/// Stand-in for an external telemetry / observability system that emits opaque JSON events
/// alongside an LLM run (RAG retrieval traces, OpenTelemetry spans, token-usage counters, ...).
/// The agent has no schema for these — it just forwards them to the client as AG-UI
/// <see cref="AGUI.Abstractions.RawEvent"/> instances so the UI can store, log, or render
/// them without the agent needing to know what they mean.
/// </summary>
internal sealed class TelemetrySource
{
    private static readonly JsonElement RagRetrieval = ParseLiteral("""
        {
          "kind": "rag.retrieval",
          "query": "ag-ui raw events",
          "documentIds": ["doc_intro", "doc_protocol"],
          "latencyMs": 87
        }
        """);

    private static readonly JsonElement RagRerank = ParseLiteral("""
        {
          "kind": "rag.rerank",
          "model": "rerank-v2",
          "latencyMs": 12
        }
        """);

    private static readonly JsonElement TraceSpan = ParseLiteral("""
        {
          "kind": "trace.span",
          "name": "llm.generate",
          "spanId": "span_42",
          "durationMs": 134
        }
        """);

    private static readonly JsonElement MetricsUsage = ParseLiteral("""
        {
          "kind": "metrics.usage",
          "promptTokens": 412,
          "completionTokens": 78
        }
        """);

#pragma warning disable CA1822 // Suppressed so users can later inject per-request telemetry without changing the API shape.
    public IEnumerable<RawTelemetry> GetPreCallEvents()
    {
        yield return new RawTelemetry("rag", RagRetrieval);
        yield return new RawTelemetry("rag", RagRerank);
    }

    public IEnumerable<RawTelemetry> GetPostCallEvents()
    {
        yield return new RawTelemetry("trace", TraceSpan);
        yield return new RawTelemetry("metrics", MetricsUsage);
    }
#pragma warning restore CA1822

    private static JsonElement ParseLiteral(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}

internal sealed class RawTelemetry
{
    public RawTelemetry(string source, JsonElement payload)
    {
        Source = source;
        Payload = payload;
    }

    public string Source { get; }

    public JsonElement Payload { get; }
}
