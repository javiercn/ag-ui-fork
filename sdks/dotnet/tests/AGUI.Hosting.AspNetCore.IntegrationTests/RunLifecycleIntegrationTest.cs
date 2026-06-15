using AGUI.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AGUI.Hosting.AspNetCore.IntegrationTests;

public sealed class RunLifecycleIntegrationTest : IntegrationTestBase
{
    public RunLifecycleIntegrationTest(WebApplicationFactory<Program> factory)
        : base(factory)
    {
    }

    [Fact]
    public async Task PostRun_EmptyStream_AutoGeneratesRunLifecycleEvents()
    {
        var client = CreateClient((messages, options, ct) => EmitEmptyResponse(ct));

        var updates = await CollectUpdates(client, [new ChatMessage(ChatRole.User, "Hi")]);

        Assert.Collection(updates,
            u =>
            {
                Assert.Equal(ChatRole.Assistant, u.Role);
                Assert.NotNull(u.ConversationId);
                Assert.NotNull(u.ResponseId);
                var started = Assert.IsType<RunStartedEvent>(u.RawRepresentation);
                Assert.Equal(u.ConversationId, started.ThreadId);
                Assert.Equal(u.ResponseId, started.RunId);
            },
            u =>
            {
                Assert.Equal(ChatRole.Assistant, u.Role);
                Assert.Equal(ChatFinishReason.Stop, u.FinishReason);
                var finished = Assert.IsType<RunFinishedEvent>(u.RawRepresentation);
                Assert.Equal(u.ConversationId, finished.ThreadId);
                Assert.Equal(u.ResponseId, finished.RunId);
            });
    }
}
