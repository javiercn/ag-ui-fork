using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AGUI.Hosting.AspNetCore.IntegrationTests;

public abstract class IntegrationTestBase<TEntryPoint> : IClassFixture<WebApplicationFactory<TEntryPoint>>
    where TEntryPoint : class
{
    private readonly WebApplicationFactory<TEntryPoint> _factory;

    protected WebApplicationFactory<TEntryPoint> Factory => _factory;

    protected IntegrationTestBase(WebApplicationFactory<TEntryPoint> factory)
    {
        _factory = factory;
    }

    protected static async Task<List<ChatResponseUpdate>> CollectUpdates(
        AGUIChatClient client, IList<ChatMessage> messages, ChatOptions? options = null)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages, options).ConfigureAwait(false))
        {
            updates.Add(update);
        }

        return updates;
    }

    protected static string ExtractText(List<ChatResponseUpdate> updates)
    {
        return string.Concat(updates
            .Where(u => !string.IsNullOrEmpty(u.Text))
            .Select(u => u.Text));
    }

    protected static async IAsyncEnumerable<ChatResponseUpdate> EmitEmptyResponse(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    protected static async IAsyncEnumerable<ChatResponseUpdate> EmitTextResponse(
        string text,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            MessageId = Guid.NewGuid().ToString("N"),
            Contents = [new TextContent(text)]
        };
        await Task.CompletedTask.ConfigureAwait(false);
    }

    protected static async IAsyncEnumerable<ChatResponseUpdate> EmitToolCallResponse(
        string toolCallId, string toolCallName, IDictionary<string, object?>? arguments,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent(toolCallId, toolCallName, arguments)],
            FinishReason = ChatFinishReason.ToolCalls
        };
        await Task.CompletedTask.ConfigureAwait(false);
    }

    protected static async IAsyncEnumerable<ChatResponseUpdate> EmitToolCallWithResultResponse(
        string toolCallId, string toolCallName, IDictionary<string, object?>? arguments, object? result,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent(toolCallId, toolCallName, arguments)],
            FinishReason = ChatFinishReason.ToolCalls
        };
        yield return new ChatResponseUpdate
        {
            Contents = [new FunctionResultContent(toolCallId, result)]
        };
        await Task.CompletedTask.ConfigureAwait(false);
    }

    protected static AGUITool CreateToolDeclaration(string name, string? description = null)
    {
        return new AGUITool
        {
            Name = name,
            Description = description ?? $"Tool {name}",
            Parameters = System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" })
        };
    }
}

public abstract class IntegrationTestBase : IntegrationTestBase<Program>
{
    protected IntegrationTestBase(WebApplicationFactory<Program> factory)
        : base(factory)
    {
        Environment.SetEnvironmentVariable(
            "ASPNETCORE_TEST_CONTENTROOT_AGUI_HOSTING_ASPNETCORE_INTEGRATIONTESTS",
            AppContext.BaseDirectory);
    }

    protected AGUIChatClient CreateClient(
        Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> handler)
    {
        var httpClient = Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(sp =>
                {
                    var client = new DelegatingStreamingChatClient();
                    client.SetHandler(handler);
                    return client;
                });
            });
        }).CreateClient();

        return new AGUIChatClient(httpClient, "/agui");
    }
}
