using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AGUI.Abstractions;
#if NET
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
#endif

namespace AGUI.Client;

internal sealed class AGUIHttpTransport : IAGUITransport
{
    private readonly HttpClient _client;
    private readonly string _endpoint;

    internal AGUIHttpTransport(HttpClient client, string endpoint)
    {
        _client = client;
        _endpoint = endpoint;
    }

    public async IAsyncEnumerable<BaseEvent> SendAsync(
        RunAgentInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
#if NET
        using HttpRequestMessage request = new(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(input, AGUIJsonSerializerContext.Default.RunAgentInput)
        };

        using HttpResponseMessage response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var items = SseParser.Create(responseStream, ItemParser).EnumerateAsync(cancellationToken);

        await foreach (var sseItem in items.ConfigureAwait(false))
        {
            yield return sseItem.Data;
        }
#else
        throw new PlatformNotSupportedException("SSE streaming requires .NET 8.0 or later.");
#pragma warning disable CS0162 // Unreachable code
        await Task.CompletedTask;
        yield break;
#pragma warning restore CS0162
#endif
    }

#if NET
    private static BaseEvent ItemParser(string type, ReadOnlySpan<byte> data)
    {
        return JsonSerializer.Deserialize(data, AGUIJsonSerializerContext.Default.BaseEvent) ??
            throw new InvalidOperationException("Failed to deserialize SSE item.");
    }
#endif
}
