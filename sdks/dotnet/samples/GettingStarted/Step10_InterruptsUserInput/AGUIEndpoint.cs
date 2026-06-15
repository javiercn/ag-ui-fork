using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Hosting.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Step10_InterruptsUserInput;

internal static class AGUIEndpoint
{
    internal static IEndpointConventionBuilder MapAGUI(
        this IEndpointRouteBuilder endpoints,
        string pattern)
    {
        return endpoints.MapPost(pattern, (
            [FromBody] RunAgentInput input,
            [FromServices] IChatClient chatClient,
            [FromServices] IOptions<JsonOptions> jsonOptions,
            CancellationToken cancellationToken) =>
        {
            var jsonSerializerOptions = jsonOptions.Value.SerializerOptions;
            var ctx = input.ToChatRequestContext(jsonSerializerOptions);

            var events = HandleUserInputFlowAsync(
                chatClient, ctx, input, jsonSerializerOptions, cancellationToken);

            return TypedResults.ServerSentEvents(WrapAsSseItems(events, cancellationToken));
        });
    }

    private static async IAsyncEnumerable<BaseEvent> HandleUserInputFlowAsync(
        IChatClient chatClient,
        ChatRequestContext ctx,
        RunAgentInput input,
        JsonSerializerOptions jsonSerializerOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new RunStartedEvent
        {
            ThreadId = input.ThreadId,
            RunId = input.RunId
        };

        string? userText = ctx.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text?.ToUpperInvariant();

        // Check if this is a resume from a previous interrupt
        if (input.State is { ValueKind: JsonValueKind.Object } state &&
            state.TryGetProperty("interruptResponse", out JsonElement interruptResponse))
        {
            string response = "unknown";

            if (interruptResponse.TryGetProperty("response", out JsonElement responseElement))
            {
                response = responseElement.GetString() ?? "unknown";
            }

            string prompt = "unknown";
            if (interruptResponse.TryGetProperty("prompt", out JsonElement promptElement))
            {
                prompt = promptElement.GetString() ?? "unknown";
            }

            string messageId = $"msg_{Guid.NewGuid():N}";
            string text = $"Thank you! You provided '{response}' for '{prompt}'. Processing your input...";

            yield return new TextMessageStartEvent
            {
                MessageId = messageId,
                Role = AGUIRoles.Assistant
            };

            yield return new TextMessageContentEvent
            {
                MessageId = messageId,
                Delta = text
            };

            yield return new TextMessageEndEvent
            {
                MessageId = messageId
            };

            yield return new RunFinishedEvent
            {
                ThreadId = input.ThreadId,
                RunId = input.RunId,
                Outcome = new RunFinishedSuccessOutcome()
            };

            yield break;
        }

        // Normal flow: check for trigger phrases
        if (userText?.Contains("SETUP", StringComparison.Ordinal) == true ||
            userText?.Contains("CONFIGURE", StringComparison.Ordinal) == true)
        {
            string interruptId = $"input_{Guid.NewGuid():N}";
            string messageId = $"msg_{Guid.NewGuid():N}";

            yield return new TextMessageStartEvent
            {
                MessageId = messageId,
                Role = AGUIRoles.Assistant
            };

            yield return new TextMessageContentEvent
            {
                MessageId = messageId,
                Delta = "I need some additional information to complete the setup."
            };

            yield return new TextMessageEndEvent
            {
                MessageId = messageId
            };

            var metadata = JsonSerializer.SerializeToElement(
                new
                {
                    type = "user_input",
                    prompt = "Please enter your preferred username:",
                    inputType = "text",
                    required = true
                },
                jsonSerializerOptions.GetTypeInfo(typeof(object)));

            yield return new RunFinishedEvent
            {
                ThreadId = input.ThreadId,
                RunId = input.RunId,
                Outcome = new RunFinishedInterruptOutcome
                {
                    Interrupts =
                    [
                        new AGUIInterrupt
                        {
                            Id = interruptId,
                            Reason = InterruptReasons.InputRequired,
                            Message = "Please enter your preferred username:",
                            Metadata = metadata
                        }
                    ]
                }
            };
        }
        else if (userText?.Contains("EMAIL", StringComparison.Ordinal) == true ||
                 userText?.Contains("CONTACT", StringComparison.Ordinal) == true)
        {
            string interruptId = $"input_{Guid.NewGuid():N}";
            string messageId = $"msg_{Guid.NewGuid():N}";

            yield return new TextMessageStartEvent
            {
                MessageId = messageId,
                Role = AGUIRoles.Assistant
            };

            yield return new TextMessageContentEvent
            {
                MessageId = messageId,
                Delta = "I need your contact information to proceed."
            };

            yield return new TextMessageEndEvent
            {
                MessageId = messageId
            };

            var metadata = JsonSerializer.SerializeToElement(
                new
                {
                    type = "user_input",
                    prompt = "Please enter your email address:",
                    inputType = "email",
                    required = true
                },
                jsonSerializerOptions.GetTypeInfo(typeof(object)));

            yield return new RunFinishedEvent
            {
                ThreadId = input.ThreadId,
                RunId = input.RunId,
                Outcome = new RunFinishedInterruptOutcome
                {
                    Interrupts =
                    [
                        new AGUIInterrupt
                        {
                            Id = interruptId,
                            Reason = InterruptReasons.InputRequired,
                            Message = "Please enter your email address:",
                            Metadata = metadata
                        }
                    ]
                }
            };
        }
        else
        {
            // Normal LLM response
            await foreach (var evt in chatClient.GetStreamingResponseAsync(
                    ctx.Messages, ctx.ChatOptions, cancellationToken)
                .AsAGUIEventStreamAsync(ctx, cancellationToken).ConfigureAwait(false))
            {
                if (evt is RunStartedEvent or RunFinishedEvent)
                {
                    continue;
                }

                yield return evt;
            }

            yield return new RunFinishedEvent
            {
                ThreadId = input.ThreadId,
                RunId = input.RunId,
                Outcome = new RunFinishedSuccessOutcome()
            };
        }
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
