using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AGUI.Hosting.AspNetCore.IntegrationTests;

public sealed class CustomAndRawEventIntegrationTest : IntegrationTestBase
{
    public CustomAndRawEventIntegrationTest(WebApplicationFactory<Program> factory)
        : base(factory)
    {
    }

    [Fact]
    public async Task PostRun_CustomEvent_MapsToUpdateWithRawRepresentation()
    {
        var client = CreateClient((messages, options, ct) => EmitCustomEventResponse(ct));

        var updates = await CollectUpdates(client, [new ChatMessage(ChatRole.User, "Hi")]);

        // Expect: RunStarted, CustomEvent, RunFinished
        Assert.Equal(3, updates.Count);

        var customUpdate = updates[1];
        Assert.Equal(ChatRole.Assistant, customUpdate.Role);
        var custom = Assert.IsType<CustomEvent>(customUpdate.RawRepresentation);
        Assert.Equal("user_preference_updated", custom.Name);
        Assert.Equal("dark", custom.Value!.Value.GetProperty("theme").GetString());
    }

    [Fact]
    public async Task PostRun_RawEvent_MapsToUpdateWithRawRepresentation()
    {
        var client = CreateClient((messages, options, ct) => EmitRawEventResponse(ct));

        var updates = await CollectUpdates(client, [new ChatMessage(ChatRole.User, "Hi")]);

        // Expect: RunStarted, RawEvent, RunFinished
        Assert.Equal(3, updates.Count);

        var rawUpdate = updates[1];
        Assert.Equal(ChatRole.Assistant, rawUpdate.Role);
        var raw = Assert.IsType<RawEvent>(rawUpdate.RawRepresentation);
        Assert.Equal("frontend", raw.Source);
        Assert.Equal("button_click", raw.Event.GetProperty("action").GetString());
    }

    [Fact]
    public async Task PostRun_CustomAndRawEvents_ShareConversationAndResponseIds()
    {
        var client = CreateClient((messages, options, ct) => EmitCustomAndRawResponse(ct));

        var updates = await CollectUpdates(client, [new ChatMessage(ChatRole.User, "Hi")]);

        // Expect: RunStarted, CustomEvent, RawEvent, RunFinished
        Assert.Equal(4, updates.Count);

        Assert.All(updates, u =>
        {
            Assert.NotNull(u.ConversationId);
            Assert.NotNull(u.ResponseId);
        });

        var conversationId = updates[0].ConversationId;
        Assert.All(updates, u => Assert.Equal(conversationId, u.ConversationId));
    }

    [Fact]
    public async Task PostRun_RawEventWithoutSource_SourceIsNull()
    {
        var client = CreateClient((messages, options, ct) => EmitRawEventWithoutSourceResponse(ct));

        var updates = await CollectUpdates(client, [new ChatMessage(ChatRole.User, "Hi")]);

        Assert.Equal(3, updates.Count);
        var raw = Assert.IsType<RawEvent>(updates[1].RawRepresentation);
        Assert.Null(raw.Source);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmitCustomEventResponse(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            RawRepresentation = new CustomEvent
            {
                Name = "user_preference_updated",
                Value = JsonSerializer.SerializeToElement(new { theme = "dark", notifications = true })
            }
        };
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmitRawEventResponse(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            RawRepresentation = new RawEvent
            {
                Event = JsonSerializer.SerializeToElement(new { action = "button_click", elementId = "submit-btn" }),
                Source = "frontend"
            }
        };
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmitCustomAndRawResponse(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            RawRepresentation = new CustomEvent
            {
                Name = "test_custom",
                Value = JsonSerializer.SerializeToElement(new { key = "value" })
            }
        };
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            RawRepresentation = new RawEvent
            {
                Event = JsonSerializer.SerializeToElement(new { data = "test" }),
                Source = "external"
            }
        };
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmitRawEventWithoutSourceResponse(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            RawRepresentation = new RawEvent
            {
                Event = JsonSerializer.SerializeToElement(new { data = "no source" })
            }
        };
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
