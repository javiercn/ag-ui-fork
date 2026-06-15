using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Hosting.AspNetCore;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace AGUIDojoServer;

internal static class AGUIAgentEndpoint
{
    internal static IEndpointConventionBuilder MapAGUIAgent(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        AIAgent aiAgent,
        AgentSessionStore sessionStore)
    {
        return endpoints.MapPost(pattern, (
            [FromBody] RunAgentInput input,
            [FromServices] IOptions<JsonOptions> jsonOptions,
            CancellationToken cancellationToken) =>
        {
            var jsonSerializerOptions = jsonOptions.Value.SerializerOptions;

            var ctx = input.ToChatRequestContext(jsonSerializerOptions);

            // Handle resume payloads if present (continuing from an interrupt).
            // AG-UI allows multiple resumes per turn; collapse them into one tool message.
            if (input.Resume is { Count: > 0 } resumes)
            {
                foreach (var resume in resumes)
                {
                    var resumeContent = new FunctionResultContent(
                        resume.InterruptId ?? string.Empty,
                        resume.Payload);
                    ctx.Messages.Add(new ChatMessage(ChatRole.User, [resumeContent]));
                }
            }

            // Wrap the AG-UI ChatOptions in a ChatClientAgentRunOptions so the inner
            // ChatClientAgent picks up the approval-flow-wrapped client tools and the
            // stashed agui_input.
            ctx.ChatOptions.AdditionalProperties!["ag_ui_agent_input"] = input;
            var runOptions = new ChatClientAgentRunOptions
            {
                ChatOptions = ctx.ChatOptions,
            };

            // Run the agent, convert AgentResponseUpdate → ChatResponseUpdate,
            // map framework events (ExecutorInvoked → StepStarted, etc.),
            // then convert to AG-UI event stream.
            var updates = aiAgent.RunStreamingAsync(
                    ctx.Messages,
                    options: runOptions,
                    cancellationToken: cancellationToken)
                .AsChatResponseUpdatesAsync()
                .MapAgentFrameworkEventsAsync()
                .AsAGUIEventStreamAsync(ctx, cancellationToken);

            return TypedResults.ServerSentEvents(WrapAsSseItems(updates, cancellationToken));
        });
    }

    internal static IEndpointConventionBuilder MapAGUIAgent(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        AIAgent aiAgent,
        AgentSessionStore sessionStore,
        bool useSession)
    {
        if (!useSession)
        {
            return MapAGUIAgent(endpoints, pattern, aiAgent, sessionStore);
        }

        return endpoints.MapPost(pattern, async (
            [FromBody] RunAgentInput input,
            [FromServices] IOptions<JsonOptions> jsonOptions,
            CancellationToken cancellationToken) =>
        {
            var jsonSerializerOptions = jsonOptions.Value.SerializerOptions;

            var ctx = input.ToChatRequestContext(jsonSerializerOptions);

            if (input.Resume is { Count: > 0 } resumes)
            {
                foreach (var resume in resumes)
                {
                    var resumeContent = new FunctionResultContent(
                        resume.InterruptId ?? string.Empty,
                        resume.Payload);
                    ctx.Messages.Add(new ChatMessage(ChatRole.User, [resumeContent]));
                }
            }

            var session = await sessionStore.GetSessionAsync(aiAgent, input.ThreadId, cancellationToken).ConfigureAwait(false);

            ctx.ChatOptions.AdditionalProperties!["ag_ui_agent_input"] = input;
            var runOptions = new ChatClientAgentRunOptions
            {
                ChatOptions = ctx.ChatOptions,
            };

            var updates = aiAgent.RunStreamingAsync(
                    ctx.Messages,
                    session: session,
                    options: runOptions,
                    cancellationToken: cancellationToken)
                .AsChatResponseUpdatesAsync()
                .MapAgentFrameworkEventsAsync()
                .AsAGUIEventStreamAsync(ctx, cancellationToken);

            var sessionPersistingEvents = WrapWithSessionPersistenceAsync(
                updates,
                aiAgent,
                input.ThreadId,
                session,
                sessionStore,
                cancellationToken);

            return TypedResults.ServerSentEvents(WrapAsSseItems(sessionPersistingEvents, cancellationToken));
        });
    }

    /// <summary>
    /// Maps agent-framework-specific events to AG-UI events.
    /// </summary>
    /// <remarks>
    /// The <see cref="AgentResponseExtensions.AsChatResponseUpdatesAsync"/> method wraps each
    /// <see cref="AgentResponseUpdate"/> into a <see cref="ChatResponseUpdate"/> with
    /// <see cref="ChatResponseUpdate.RawRepresentation"/> = <see cref="AgentResponseUpdate"/>.
    /// Framework events like <see cref="ExecutorInvokedEvent"/> and <see cref="ExecutorCompletedEvent"/>
    /// are stored in <see cref="AgentResponseUpdate.RawRepresentation"/>.
    /// This method unwraps them and maps to the corresponding AG-UI events:
    /// <list type="bullet">
    /// <item><see cref="ExecutorInvokedEvent"/> → <see cref="StepStartedEvent"/></item>
    /// <item><see cref="ExecutorCompletedEvent"/> → <see cref="StepFinishedEvent"/></item>
    /// <item>Any <see cref="BaseEvent"/> in RawRepresentation is promoted to the ChatResponseUpdate level</item>
    /// </list>
    /// </remarks>
    internal static async IAsyncEnumerable<ChatResponseUpdate> MapAgentFrameworkEventsAsync(
        this IAsyncEnumerable<ChatResponseUpdate> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in updates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // Check if this ChatResponseUpdate wraps an AgentResponseUpdate
            if (update.RawRepresentation is AgentResponseUpdate agentUpdate)
            {
                var innerRaw = agentUpdate.RawRepresentation;

                // If the inner RawRepresentation is already a BaseEvent (e.g., StateSnapshotEvent, CustomEvent),
                // promote it so AsAGUIEventStreamAsync can emit it directly
                if (innerRaw is BaseEvent baseEvent)
                {
                    yield return new ChatResponseUpdate { RawRepresentation = baseEvent };
                    continue;
                }

                // Map ExecutorInvokedEvent → StepStartedEvent
                if (innerRaw is ExecutorInvokedEvent executorInvoked)
                {
                    yield return new ChatResponseUpdate
                    {
                        RawRepresentation = new StepStartedEvent
                        {
                            StepName = executorInvoked.ExecutorId,
                        }
                    };
                    continue;
                }

                // Map ExecutorCompletedEvent → StepFinishedEvent
                if (innerRaw is ExecutorCompletedEvent executorCompleted)
                {
                    yield return new ChatResponseUpdate
                    {
                        RawRepresentation = new StepFinishedEvent
                        {
                            StepName = executorCompleted.ExecutorId,
                        }
                    };
                    continue;
                }
            }

            yield return update;
        }
    }

    private static async IAsyncEnumerable<BaseEvent> WrapWithSessionPersistenceAsync(
        IAsyncEnumerable<BaseEvent> events,
        AIAgent aiAgent,
        string threadId,
        AgentSession session,
        AgentSessionStore sessionStore,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }

        // Save session after streaming completes
        await sessionStore.SaveSessionAsync(aiAgent, threadId, session, cancellationToken).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<SseItem<BaseEvent>> WrapAsSseItems(
        IAsyncEnumerable<BaseEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new SseItem<BaseEvent>(evt);
        }
    }
}
