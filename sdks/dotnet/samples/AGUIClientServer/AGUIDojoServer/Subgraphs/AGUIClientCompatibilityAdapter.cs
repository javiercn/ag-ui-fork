using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace AGUIDojoServer.Subgraphs;

internal sealed class AGUIClientCompatibilityAdapter : DelegatingAIAgent
{
    private readonly JsonSerializerOptions _jsonSerializerOptions;

    private static readonly Dictionary<string, ExternalRequest> s_pendingInterrupts = new();

    public AGUIClientCompatibilityAdapter(AIAgent innerAgent, JsonSerializerOptions jsonSerializerOptions)
        : base(innerAgent)
    {
        _jsonSerializerOptions = jsonSerializerOptions;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var chatOptions = (options as ChatClientAgentRunOptions)?.ChatOptions;
        RunAgentInput? aguiInput = null;
        if (chatOptions?.AdditionalProperties?.TryGetValue("ag_ui_agent_input", out var inputObj) == true)
        {
            aguiInput = inputObj as RunAgentInput;
        }

        var threadId = aguiInput?.ThreadId ?? string.Empty;

        var transformedMessages = TransformInputMessages(messages, aguiInput);

        await foreach (var update in base.RunCoreStreamingAsync(transformedMessages, session, options, cancellationToken))
        {
            if (TryTransformRequestInfoEvent(update, threadId, out var customEvent))
            {
                yield return customEvent!;
                continue;
            }

            yield return update;
        }
    }

    private IEnumerable<ChatMessage> TransformInputMessages(IEnumerable<ChatMessage> messages, RunAgentInput? aguiInput)
    {
        if (aguiInput is null)
        {
            return messages;
        }

        if (aguiInput.ForwardedProperties.ValueKind == JsonValueKind.Object &&
            aguiInput.ForwardedProperties.TryGetProperty("command", out var command) &&
            command.ValueKind == JsonValueKind.Object &&
            command.TryGetProperty("resume", out var resume))
        {
            JsonElement resumePayload;
            if (resume.ValueKind == JsonValueKind.String)
            {
                var resumeString = resume.GetString();
                if (!string.IsNullOrEmpty(resumeString))
                {
                    try
                    {
                        resumePayload = JsonDocument.Parse(resumeString).RootElement;
                    }
                    catch (JsonException)
                    {
                        resumePayload = JsonDocument.Parse($"\"{resumeString}\"").RootElement;
                    }
                }
                else
                {
                    return messages;
                }
            }
            else
            {
                resumePayload = resume;
            }

            var threadId = aguiInput.ThreadId;
            string interruptId;
            ExternalRequest? pendingRequest = null;

            lock (s_pendingInterrupts)
            {
                if (s_pendingInterrupts.TryGetValue(threadId, out pendingRequest))
                {
                    interruptId = pendingRequest.RequestId;
                }
                else
                {
                    interruptId = TryExtractInterruptId(resumePayload);
                }
            }

            var resumeContent = new FunctionResultContent(interruptId, resumePayload)
            {
                RawRepresentation = pendingRequest
            };

            var messagesList = messages.ToList();
            messagesList.Add(new ChatMessage(ChatRole.User, [resumeContent]));
            return messagesList;
        }

        return messages;
    }

    private static string TryExtractInterruptId(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("_portId", out var portId) &&
            portId.ValueKind == JsonValueKind.String)
        {
            return portId.GetString() ?? "workflow-interrupt";
        }

        return "workflow-interrupt";
    }

    private bool TryTransformRequestInfoEvent(
        AgentResponseUpdate update,
        string threadId,
        out AgentResponseUpdate? customEvent)
    {
        customEvent = null;

        var requestInfoEvent = ExtractRequestInfoEvent(update.RawRepresentation);
        if (requestInfoEvent is null)
        {
            return false;
        }

        var request = requestInfoEvent.Request;

        if (!string.IsNullOrEmpty(threadId))
        {
            lock (s_pendingInterrupts)
            {
                s_pendingInterrupts[threadId] = request;
            }
        }

        var requestJson = JsonSerializer.Serialize(request, _jsonSerializerOptions);
        var requestElement = JsonDocument.Parse(requestJson).RootElement;

        JsonElement dataElement;
        if (requestElement.TryGetProperty("data", out var dataProp) &&
            dataProp.TryGetProperty("value", out var valueProp))
        {
            dataElement = valueProp;
        }
        else
        {
            dataElement = requestElement;
        }

        string? agent = null;
        if (dataElement.ValueKind == JsonValueKind.Object &&
            dataElement.TryGetProperty("agent", out var agentProp))
        {
            agent = agentProp.GetString();
        }

        var customValue = BuildCustomEventValue(dataElement, agent);

        // LangGraph sends the value as a JSON STRING (double-serialized)
        var valueAsJsonString = customValue.GetRawText();
        var serializedString = JsonSerializer.Serialize(valueAsJsonString);
        var valueAsJsonElement = JsonDocument.Parse(serializedString).RootElement;

        var aguiCustomEvent = new CustomEvent
        {
            Name = "on_interrupt",
            Value = valueAsJsonElement
        };

        customEvent = new AgentResponseUpdate
        {
            RawRepresentation = aguiCustomEvent
        };

        return true;
    }

    private static RequestInfoEvent? ExtractRequestInfoEvent(object? rawRepresentation)
    {
        return rawRepresentation switch
        {
            RequestInfoEvent evt => evt,
            AgentResponseUpdate agentUpdate => ExtractRequestInfoEvent(agentUpdate.RawRepresentation),
            ChatResponseUpdate chatUpdate => ExtractRequestInfoEvent(chatUpdate.RawRepresentation),
            _ => null
        };
    }

    private JsonElement BuildCustomEventValue(JsonElement dataElement, string? agent)
    {
        var valueDict = new Dictionary<string, object?>();

        if (dataElement.ValueKind == JsonValueKind.Object)
        {
            if (dataElement.TryGetProperty("message", out var message))
            {
                valueDict["message"] = message.GetString();
            }

            if (dataElement.TryGetProperty("options", out var options) &&
                options.ValueKind == JsonValueKind.Array)
            {
                var optionsList = new List<Dictionary<string, object?>>();
                foreach (var option in options.EnumerateArray())
                {
                    var optionDict = ConvertOptionToClientFormat(option, agent);
                    optionsList.Add(optionDict);
                }
                valueDict["options"] = optionsList;
            }

            if (dataElement.TryGetProperty("recommendation", out var recommendation) &&
                recommendation.ValueKind == JsonValueKind.Object)
            {
                valueDict["recommendation"] = ConvertOptionToClientFormat(recommendation, agent);
            }

            if (dataElement.TryGetProperty("agent", out var agentPropValue))
            {
                valueDict["agent"] = agentPropValue.GetString();
            }
            else if (agent is not null)
            {
                valueDict["agent"] = agent;
            }
        }

        var jsonString = JsonSerializer.Serialize(valueDict, _jsonSerializerOptions);
        return JsonDocument.Parse(jsonString).RootElement;
    }

    private static Dictionary<string, object?> ConvertOptionToClientFormat(JsonElement option, string? agent)
    {
        var result = new Dictionary<string, object?>();

        if (option.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        if (agent == "flights")
        {
            if (option.TryGetProperty("airline", out var airline))
                result["airline"] = airline.GetString();
            if (option.TryGetProperty("departure", out var departure))
                result["departure"] = departure.GetString();
            if (option.TryGetProperty("arrival", out var arrival))
                result["arrival"] = arrival.GetString();
            if (option.TryGetProperty("price", out var price))
                result["price"] = price.GetString();
            if (option.TryGetProperty("duration", out var duration))
                result["duration"] = duration.GetString();
        }
        else if (agent == "hotels")
        {
            if (option.TryGetProperty("name", out var name))
                result["name"] = name.GetString();
            if (option.TryGetProperty("location", out var location))
                result["location"] = location.GetString();
            if (option.TryGetProperty("pricePerNight", out var pricePerNight))
                result["price_per_night"] = pricePerNight.GetString();
            if (option.TryGetProperty("rating", out var rating))
                result["rating"] = rating.GetString();
        }
        else
        {
            foreach (var prop in option.EnumerateObject())
            {
                var key = prop.Name;
                result[key] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => prop.Value.GetRawText()
                };
            }
        }

        return result;
    }
}
