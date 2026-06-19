// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Amazon.Runtime.Documents;
using Amazon.Runtime.EventStreams;
using Honua.Core.Features.WorkflowPackages.Generation;
using Microsoft.Extensions.AI;

namespace Honua.Ai.Providers.Bedrock;

/// <summary>
/// An <see cref="IChatClient"/> backed by Amazon Bedrock's Converse API
/// (<c>ConverseAsync</c> / <c>ConverseStreamAsync</c>). The Converse API is used
/// (rather than the raw <c>InvokeModel</c> API) because the studio generation flows force a
/// single tool call for structured output, and Converse exposes a model-agnostic
/// <see cref="ToolConfiguration"/> / <see cref="ToolUseBlock"/> / <see cref="ToolResultBlock"/>
/// contract that we map to/from Microsoft.Extensions.AI's
/// <see cref="FunctionCallContent"/> / <see cref="FunctionResultContent"/>.
///
/// Ported from honua-devops <c>Honua.DevOps.Agent/Providers/BedrockChatClientAdapter.cs</c> so the
/// studio AI flows can run on cloud AI (AWS Bedrock / Claude) without a local Ollama/Qwen model.
/// </summary>
internal sealed class BedrockChatClientAdapter : IChatClient
{
    private readonly IAmazonBedrockRuntime _runtime;
    private readonly string _modelId;
    private readonly bool _ownsRuntime;

    internal BedrockChatClientAdapter(IAmazonBedrockRuntime runtime, string modelId, bool ownsRuntime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        _runtime = runtime;
        _modelId = modelId;
        _ownsRuntime = ownsRuntime;
    }

    /// <summary>
    /// Builds an adapter targeting the supplied <paramref name="modelId"/> in the supplied AWS
    /// <paramref name="region"/>. Authentication prefers the standard AWS credential chain (env
    /// vars, shared profile, IAM role, container/Lambda ambient credentials). A Bedrock API key
    /// (long-lived bearer token) is used when supplied via <paramref name="apiKey"/>.
    /// </summary>
    internal static BedrockChatClientAdapter Create(string modelId, string region, string? apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var effectiveRegion = string.IsNullOrWhiteSpace(region)
            ? WorkflowGenerationConfiguration.DefaultBedrockRegion
            : region;

        var config = new AmazonBedrockRuntimeConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(effectiveRegion)
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            // Explicit Bedrock API key -> use it as a static bearer token and prefer the bearer
            // auth scheme. When absent, the SDK falls back to the standard AWS credential chain.
            config.AWSTokenProvider = new ServiceBearerStaticTokenProvider(apiKey, expiration: null);
            config.AuthSchemePreference = ["httpBearerAuth"];
        }

        var runtime = new AmazonBedrockRuntimeClient(config);
        return new BedrockChatClientAdapter(runtime, modelId, ownsRuntime: true);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var request = BuildRequest(messages, options);
        var response = await _runtime
            .ConverseAsync(request, cancellationToken)
            .ConfigureAwait(false);

        List<AIContent> contents = [];
        if (response.Output?.Message?.Content is { } blocks)
        {
            foreach (var block in blocks)
            {
                var content = MapContentBlock(block);
                if (content is not null)
                {
                    contents.Add(content);
                }
            }
        }

        var responseMessage = new ChatMessage(ChatRole.Assistant, contents)
        {
            RawRepresentation = response
        };

        return new ChatResponse(responseMessage)
        {
            ModelId = _modelId,
            FinishReason = MapStopReason(response.StopReason),
            Usage = MapUsage(response.Usage),
            RawRepresentation = response
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var request = BuildStreamRequest(messages, options);
        var response = await _runtime
            .ConverseStreamAsync(request, cancellationToken)
            .ConfigureAwait(false);

        // Tool-use blocks stream their arguments incrementally as JSON text fragments keyed
        // by content-block index; accumulate per-index and emit a FunctionCallContent on stop.
        Dictionary<int, ToolUseAccumulator> toolUses = [];

        await foreach (var streamEvent in response.Stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (streamEvent)
            {
                case ContentBlockStartEvent start when start.Start?.ToolUse is { } toolUseStart:
                    toolUses[start.ContentBlockIndex ?? 0] = new ToolUseAccumulator(toolUseStart.ToolUseId, toolUseStart.Name);
                    break;

                case ContentBlockDeltaEvent delta when delta.Delta?.Text is { Length: > 0 } text:
                    yield return new ChatResponseUpdate(ChatRole.Assistant, text)
                    {
                        ModelId = _modelId,
                        RawRepresentation = streamEvent
                    };
                    break;

                case ContentBlockDeltaEvent delta when delta.Delta?.ToolUse?.Input is { } inputFragment
                    && toolUses.TryGetValue(delta.ContentBlockIndex ?? 0, out var accumulator):
                    accumulator.AppendInput(inputFragment);
                    break;

                case ContentBlockStopEvent stop when toolUses.Remove(stop.ContentBlockIndex ?? 0, out var accumulator):
                    yield return new ChatResponseUpdate(ChatRole.Assistant, [accumulator.ToFunctionCall()])
                    {
                        ModelId = _modelId,
                        RawRepresentation = streamEvent
                    };
                    break;

                case MessageStopEvent messageStop:
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        ModelId = _modelId,
                        FinishReason = MapStopReason(messageStop.StopReason),
                        RawRepresentation = streamEvent
                    };
                    break;

                case ConverseStreamMetadataEvent metadata when MapUsage(metadata.Usage) is { } usageDetails:
                    yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usageDetails)])
                    {
                        ModelId = _modelId,
                        RawRepresentation = streamEvent
                    };
                    break;
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is null && serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        if (serviceKey is null && serviceType.IsInstanceOfType(_runtime))
        {
            return _runtime;
        }

        return null;
    }

    public void Dispose()
    {
        if (_ownsRuntime)
        {
            _runtime.Dispose();
        }
    }

    // -- Request mapping (Microsoft.Extensions.AI -> Bedrock Converse) --------------------

    private ConverseRequest BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var (converseMessages, system) = MapMessages(messages);

        var request = new ConverseRequest
        {
            ModelId = options?.ModelId ?? _modelId,
            Messages = converseMessages
        };

        if (system.Count > 0)
        {
            request.System = system;
        }

        var inference = MapInference(options);
        if (inference is not null)
        {
            request.InferenceConfig = inference;
        }

        var toolConfig = MapTools(options);
        if (toolConfig is not null)
        {
            request.ToolConfig = toolConfig;
        }

        return request;
    }

    private ConverseStreamRequest BuildStreamRequest(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var (converseMessages, system) = MapMessages(messages);

        var request = new ConverseStreamRequest
        {
            ModelId = options?.ModelId ?? _modelId,
            Messages = converseMessages
        };

        if (system.Count > 0)
        {
            request.System = system;
        }

        var inference = MapInference(options);
        if (inference is not null)
        {
            request.InferenceConfig = inference;
        }

        var toolConfig = MapTools(options);
        if (toolConfig is not null)
        {
            request.ToolConfig = toolConfig;
        }

        return request;
    }

    private static InferenceConfiguration? MapInference(ChatOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        InferenceConfiguration? inference = null;

        if (options.MaxOutputTokens is { } maxTokens)
        {
            inference ??= new InferenceConfiguration();
            inference.MaxTokens = maxTokens;
        }

        if (options.Temperature is { } temperature)
        {
            inference ??= new InferenceConfiguration();
            inference.Temperature = temperature;
        }

        if (options.TopP is { } topP)
        {
            inference ??= new InferenceConfiguration();
            inference.TopP = topP;
        }

        return inference;
    }

    private static ToolConfiguration? MapTools(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 } tools)
        {
            return null;
        }

        List<Tool> converseTools = [];
        foreach (var tool in tools)
        {
            if (tool is not AIFunction function)
            {
                // Bedrock Converse only models function-style tools.
                continue;
            }

            converseTools.Add(new Tool
            {
                ToolSpec = new ToolSpecification
                {
                    Name = function.Name,
                    Description = string.IsNullOrWhiteSpace(function.Description) ? function.Name : function.Description,
                    InputSchema = new ToolInputSchema
                    {
                        Json = JsonElementToDocument(function.JsonSchema)
                    }
                }
            });
        }

        var config = converseTools.Count > 0 ? new ToolConfiguration { Tools = converseTools } : null;
        if (config is null)
        {
            return null;
        }

        // Honor a forced/required tool choice so structured-output flows always get a tool_use
        // block. Bedrock models a specific required tool via ToolChoice.Tool.
        if (options.ToolMode is RequiredChatToolMode { RequiredFunctionName: { Length: > 0 } requiredName })
        {
            config.ToolChoice = new ToolChoice { Tool = new SpecificToolChoice { Name = requiredName } };
        }

        return config;
    }

    private static (List<Message> Messages, List<SystemContentBlock> System) MapMessages(IEnumerable<ChatMessage> messages)
    {
        List<Message> converseMessages = [];
        List<SystemContentBlock> system = [];

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                if (!string.IsNullOrEmpty(message.Text))
                {
                    system.Add(new SystemContentBlock { Text = message.Text });
                }

                continue;
            }

            var content = MapOutgoingContent(message);
            if (content.Count == 0)
            {
                continue;
            }

            // Bedrock recognizes only "user" and "assistant" roles. Tool results are carried
            // as ToolResult blocks inside a user-role message.
            var role = message.Role == ChatRole.Assistant
                ? ConversationRole.Assistant
                : ConversationRole.User;

            converseMessages.Add(new Message { Role = role, Content = content });
        }

        return (converseMessages, system);
    }

    private static List<ContentBlock> MapOutgoingContent(ChatMessage message)
    {
        List<ContentBlock> blocks = [];

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent { Text.Length: > 0 } text:
                    blocks.Add(new ContentBlock { Text = text.Text });
                    break;

                case FunctionCallContent call:
                    blocks.Add(new ContentBlock
                    {
                        ToolUse = new ToolUseBlock
                        {
                            ToolUseId = call.CallId,
                            Name = call.Name,
                            Input = ArgumentsToDocument(call.Arguments)
                        }
                    });
                    break;

                case FunctionResultContent result:
                    blocks.Add(new ContentBlock
                    {
                        ToolResult = new ToolResultBlock
                        {
                            ToolUseId = result.CallId,
                            Content =
                            [
                                new ToolResultContentBlock { Text = ResultToText(result.Result) }
                            ]
                        }
                    });
                    break;
            }
        }

        return blocks;
    }

    // -- Response mapping (Bedrock Converse -> Microsoft.Extensions.AI) -------------------

    private static AIContent? MapContentBlock(ContentBlock block)
    {
        if (block.Text is { Length: > 0 } text)
        {
            return new TextContent(text);
        }

        if (block.ToolUse is { } toolUse)
        {
            return new FunctionCallContent(
                callId: toolUse.ToolUseId,
                name: toolUse.Name,
                arguments: DocumentToArguments(toolUse.Input));
        }

        return null;
    }

    private static ChatFinishReason? MapStopReason(StopReason? stopReason)
    {
        if (stopReason is null)
        {
            return null;
        }

        if (stopReason == StopReason.Tool_use)
        {
            return ChatFinishReason.ToolCalls;
        }

        if (stopReason == StopReason.Max_tokens)
        {
            return ChatFinishReason.Length;
        }

        if (stopReason == StopReason.Content_filtered || stopReason == StopReason.Guardrail_intervened)
        {
            return ChatFinishReason.ContentFilter;
        }

        return ChatFinishReason.Stop;
    }

    private static UsageDetails? MapUsage(TokenUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return new UsageDetails
        {
            InputTokenCount = usage.InputTokens,
            OutputTokenCount = usage.OutputTokens,
            TotalTokenCount = usage.TotalTokens ?? (usage.InputTokens + usage.OutputTokens)
        };
    }

    private static string ResultToText(object? result)
    {
        return result switch
        {
            null => string.Empty,
            string s => s,
            JsonElement json => json.ValueKind == JsonValueKind.String ? json.GetString() ?? string.Empty : json.GetRawText(),
            _ => JsonSerializer.Serialize(result)
        };
    }

    // -- JSON <-> Bedrock Document conversion --------------------------------------------

    private static Document ArgumentsToDocument(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return new Document(new Dictionary<string, Document>());
        }

        Dictionary<string, Document> map = new(arguments.Count);
        foreach (var (key, value) in arguments)
        {
            map[key] = ObjectToDocument(value);
        }

        return new Document(map);
    }

    private static Dictionary<string, object?> DocumentToArguments(Document document)
    {
        Dictionary<string, object?> arguments = [];
        if (document.Type == DocumentType.Dictionary)
        {
            foreach (var (key, value) in document.AsDictionary())
            {
                arguments[key] = DocumentToObject(value);
            }
        }

        return arguments;
    }

    private static Document JsonElementToDocument(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                Dictionary<string, Document> map = [];
                foreach (var property in element.EnumerateObject())
                {
                    map[property.Name] = JsonElementToDocument(property.Value);
                }

                return new Document(map);

            case JsonValueKind.Array:
                List<Document> list = [];
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(JsonElementToDocument(item));
                }

                return new Document(list);

            case JsonValueKind.String:
                return new Document(element.GetString() ?? string.Empty);

            case JsonValueKind.Number:
                if (element.TryGetInt64(out var longValue))
                {
                    return new Document(longValue);
                }

                return new Document(element.GetDouble());

            case JsonValueKind.True:
                return new Document(true);

            case JsonValueKind.False:
                return new Document(false);

            default:
                return new Document();
        }
    }

    private static Document ObjectToDocument(object? value)
    {
        return value switch
        {
            null => new Document(),
            JsonElement json => JsonElementToDocument(json),
            string s => new Document(s),
            bool b => new Document(b),
            int i => new Document(i),
            long l => new Document(l),
            double d => new Document(d),
            float f => new Document(f),
            _ => JsonElementToDocument(JsonSerializer.SerializeToElement(value))
        };
    }

    private static object? DocumentToObject(Document document)
    {
        switch (document.Type)
        {
            case DocumentType.String:
                return document.AsString();
            case DocumentType.Bool:
                return document.AsBool();
            case DocumentType.Int:
                return document.AsInt();
            case DocumentType.Long:
                return document.AsLong();
            case DocumentType.Double:
                return document.AsDouble();
            case DocumentType.List:
                List<object?> list = [];
                foreach (var item in document.AsList())
                {
                    list.Add(DocumentToObject(item));
                }

                return list;
            case DocumentType.Dictionary:
                Dictionary<string, object?> map = [];
                foreach (var (key, value) in document.AsDictionary())
                {
                    map[key] = DocumentToObject(value);
                }

                return map;
            default:
                return null;
        }
    }

    private sealed class ToolUseAccumulator(string toolUseId, string name)
    {
        private readonly System.Text.StringBuilder _input = new();

        internal void AppendInput(string fragment) => _input.Append(fragment);

        internal FunctionCallContent ToFunctionCall()
        {
            var arguments = ParseInput();
            return new FunctionCallContent(toolUseId, name, arguments);
        }

        private Dictionary<string, object?> ParseInput()
        {
            var json = _input.ToString();
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Dictionary<string, object?>();
            }

            try
            {
                using var parsed = JsonDocument.Parse(json);
                return DocumentToArguments(JsonElementToDocument(parsed.RootElement));
            }
            catch (JsonException)
            {
                return new Dictionary<string, object?> { ["__raw"] = json };
            }
        }
    }
}
