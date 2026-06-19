// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using Honua.Ai.WorkflowGeneration.Models;
using Honua.Ai.WorkflowGeneration.Prompts;
using Honua.Core.Features.WorkflowPackages.Generation;
using Honua.Core.Features.WorkflowPackages.Generation.Abstractions;
using Honua.Core.Features.WorkflowPackages.Generation.Domain;
using Honua.Infrastructure.WorkflowGeneration;
using Honua.ServiceDefaults;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Ai.WorkflowGeneration;

/// <summary>
/// Workflow generation provider for Azure OpenAI (provider id <c>azureopenai</c>). Mirrors the
/// AWS Bedrock provider (<c>BedrockWorkflowGenerationProvider</c>, ai-studio-bedrock-provider):
/// structured output is obtained by forcing a single tool call (<c>emit_workflow</c>) whose
/// <c>input_schema</c> is the constrained proposal schema, over a <see cref="IChatClient"/> built by
/// the <see cref="IAzureOpenAiChatClientFactory"/> seam.
/// </summary>
/// <remarks>
/// This type is cloud-neutral: the <c>Azure.AI.OpenAI</c> SDK surface is confined to the factory
/// implementation in <c>Honua.Azure</c> (bound by the composition root). Authentication prefers
/// Entra managed identity; an optional key fallback comes from
/// <c>HONUA_WORKFLOWGEN_AZUREOPENAI_API_KEY</c> (handled by the config PostConfigure step). When the
/// Azure factory is not registered (Azure module not compiled in) the provider reports
/// <see cref="IsConfigured"/> = false and is unselectable.
/// </remarks>
internal sealed class AzureOpenAiWorkflowGenerationProvider : IWorkflowGenerationProvider
{
    private const string ToolName = "emit_workflow";

    private readonly string _providerId;
    private readonly IAzureOpenAiChatClientFactory? _chatClientFactory;
    private readonly WorkflowGenerationConfiguration _configuration;
    private readonly ILogger<AzureOpenAiWorkflowGenerationProvider> _logger;

    public AzureOpenAiWorkflowGenerationProvider(
        string providerId,
        IOptions<WorkflowGenerationConfiguration> options,
        ILogger<AzureOpenAiWorkflowGenerationProvider> logger,
        IAzureOpenAiChatClientFactory? chatClientFactory = null)
    {
        _providerId = providerId;
        _chatClientFactory = chatClientFactory;
        _configuration = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => _providerId;

    /// <inheritdoc />
    public bool IsConfigured
    {
        get
        {
            // Azure OpenAI needs an endpoint + deployment (Model) and the Azure-side factory to be
            // registered (managed identity supplies auth; no key required). When Honua.Azure is not
            // compiled in, _chatClientFactory is null and the provider is unselectable.
            if (_chatClientFactory is null)
            {
                return false;
            }

            var options = _configuration.GetProvider(_providerId);
            return options is not null
                && !string.IsNullOrWhiteSpace(options.Endpoint)
                && !string.IsNullOrWhiteSpace(options.Model);
        }
    }

    /// <inheritdoc />
    public async Task<WorkflowGenerationProposal> GenerateAsync(
        WorkflowGenerationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_chatClientFactory is null)
        {
            return WorkflowGenerationProposal.Error(
                $"Provider '{_providerId}' requires the Azure module (HonuaIncludeAzure) to be enabled.",
                _providerId);
        }

        var options = _configuration.GetProvider(_providerId);
        if (options is null || string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.Model))
        {
            return WorkflowGenerationProposal.Error($"Provider '{_providerId}' is not configured.", _providerId);
        }

        var model = string.IsNullOrWhiteSpace(request.ModelOverride) ? options.Model : request.ModelOverride!;

        using var activity = HonuaTelemetry.ActivitySource.StartActivity("honua.workflowgen.generate");
        activity?.SetTag("workflowgen.provider", _providerId);
        activity?.SetTag("workflowgen.model", model);
        WorkflowGenerationLog.GenerationRequested(_logger, _providerId, model);

        var stopwatch = Stopwatch.StartNew();
        IChatClient? client = null;
        try
        {
            client = _chatClientFactory.Create(
                options.Endpoint,
                model,
                string.IsNullOrWhiteSpace(options.ApiVersion) ? null : options.ApiVersion,
                string.IsNullOrWhiteSpace(options.ApiKey) ? null : options.ApiKey);

            var schema = WorkflowGenerationSchema.Build(request.Registry.Nodes);
            var tool = new EmitWorkflowFunction(schema);
            var chatOptions = new ChatOptions
            {
                ModelId = model,
                MaxOutputTokens = options.MaxTokens,
                Temperature = 0.0f,
                Tools = [tool],
                ToolMode = ChatToolMode.RequireSpecific(ToolName)
            };

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, WorkflowGenerationPrompt.BuildSystem(request)),
                new(ChatRole.User, WorkflowGenerationPrompt.BuildUser(request))
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

            ChatResponse response;
            try
            {
                response = await client.GetResponseAsync(messages, chatOptions, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                WorkflowGenerationLog.GenerationFailed(_logger, _providerId, "Request timed out.");
                return WorkflowGenerationProposal.Error("Provider request timed out.", _providerId, model);
            }

            if (response.FinishReason == ChatFinishReason.ContentFilter)
            {
                const string Reason = "Provider declined the request (content filtered).";
                WorkflowGenerationLog.GenerationFailed(_logger, _providerId, Reason);
                return WorkflowGenerationProposal.Error(Reason, _providerId, model);
            }

            var call = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .FirstOrDefault(c => string.Equals(c.Name, ToolName, StringComparison.Ordinal));

            if (call is null)
            {
                const string Reason = "Provider did not return the expected tool output.";
                WorkflowGenerationLog.GenerationFailed(_logger, _providerId, Reason);
                return WorkflowGenerationProposal.Error(Reason, _providerId, model);
            }

            var proposalModel = DeserializeProposal(call.Arguments);
            if (proposalModel is null)
            {
                const string Reason = "Failed to deserialize the workflow proposal from the tool output.";
                WorkflowGenerationLog.GenerationFailed(_logger, _providerId, Reason);
                return WorkflowGenerationProposal.Error(Reason, _providerId, model);
            }

            stopwatch.Stop();
            var usage = new WorkflowGenerationUsage
            {
                PromptTokens = (int?)response.Usage?.InputTokenCount,
                CompletionTokens = (int?)response.Usage?.OutputTokenCount,
                LatencyMs = stopwatch.ElapsedMilliseconds
            };

            var proposal = WorkflowGenerationProposalMapper.ToProposal(proposalModel, _providerId, model, usage);
            WorkflowGenerationLog.GenerationProduced(
                _logger, proposal.Status, _providerId, proposal.Graph?.Nodes.Count ?? 0);
            activity?.SetTag("workflowgen.success", true);
            activity?.SetTag("workflowgen.status", proposal.Status.ToString());
            return proposal;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException ex)
        {
            WorkflowGenerationLog.GenerationFailed(_logger, _providerId, ex.Message);
            return WorkflowGenerationProposal.Error("Provider response could not be parsed.", _providerId, model);
        }
        catch (Exception ex)
        {
            WorkflowGenerationLog.GenerationFailed(_logger, _providerId, ex.Message);
            return WorkflowGenerationProposal.Error("Provider request failed.", _providerId, model);
        }
        finally
        {
            client?.Dispose();
        }
    }

    private static WorkflowGenerationModelProposal? DeserializeProposal(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return null;
        }

        // The Microsoft.Extensions.AI adapter surfaces the forced tool-call input as a
        // string->object map; write it back to JSON (AOT-safe, no reflection serialization) and
        // deserialize into the proposal DTO.
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteObject(writer, arguments);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Deserialize(WorkflowGenerationJsonContext.Default.WorkflowGenerationModelProposal);
    }

    private static void WriteObject(Utf8JsonWriter writer, IDictionary<string, object?> map)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in map)
        {
            writer.WritePropertyName(key);
            WriteValue(writer, value);
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case IDictionary<string, object?> nested:
                WriteObject(writer, nested);
                break;
            case System.Collections.IEnumerable enumerable:
                writer.WriteStartArray();
                foreach (var item in enumerable)
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    /// <summary>
    /// An <see cref="AIFunction"/> whose schema is the constrained workflow proposal schema; never
    /// invoked, it exists only to carry the schema and capture the forced tool-call input.
    /// </summary>
    private sealed class EmitWorkflowFunction : AIFunction
    {
        private readonly JsonElement _schema;

        internal EmitWorkflowFunction(JsonElement schema) => _schema = schema;

        public override string Name => ToolName;

        public override string Description => "Emit the proposed workflow.package graph (or a clarification/refusal).";

        public override JsonElement JsonSchema => _schema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<object?>(null);
    }
}
