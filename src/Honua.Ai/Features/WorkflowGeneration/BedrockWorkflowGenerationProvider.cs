// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using Honua.Ai.Providers.Bedrock;
using Honua.Ai.WorkflowGeneration.Models;
using Honua.Ai.WorkflowGeneration.Prompts;
using Honua.Core.Features.WorkflowPackages.Generation;
using Honua.Core.Features.WorkflowPackages.Generation.Abstractions;
using Honua.Core.Features.WorkflowPackages.Generation.Domain;
using Honua.ServiceDefaults;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Ai.WorkflowGeneration;

/// <summary>
/// Workflow generation provider for AWS Bedrock (Claude on Bedrock; provider id <c>bedrock</c>).
/// Structured output is obtained the same way as the Anthropic provider — forcing a single tool
/// call (<c>emit_workflow</c>) whose <c>input_schema</c> is the constrained proposal schema — but
/// over Bedrock's Converse API rather than the Anthropic Messages API. Authentication uses the AWS
/// credential chain (IAM), so no API key is required; only a model id (and optionally a region).
///
/// This lets the workflow studio flow run on cloud AI without a local Ollama/Qwen model.
/// </summary>
internal sealed class BedrockWorkflowGenerationProvider : IWorkflowGenerationProvider
{
    private const string ToolName = "emit_workflow";

    private readonly string _providerId;
    private readonly IBedrockChatClientFactory _chatClientFactory;
    private readonly WorkflowGenerationConfiguration _configuration;
    private readonly ILogger<BedrockWorkflowGenerationProvider> _logger;

    public BedrockWorkflowGenerationProvider(
        string providerId,
        IBedrockChatClientFactory chatClientFactory,
        IOptions<WorkflowGenerationConfiguration> options,
        ILogger<BedrockWorkflowGenerationProvider> logger)
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
            // Bedrock needs only a model id; the AWS credential chain (IAM) supplies auth and the
            // region falls back to the default. No endpoint/API key required.
            var options = _configuration.GetProvider(_providerId);
            return options is not null && !string.IsNullOrWhiteSpace(options.Model);
        }
    }

    /// <inheritdoc />
    public async Task<WorkflowGenerationProposal> GenerateAsync(
        WorkflowGenerationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = _configuration.GetProvider(_providerId);
        if (options is null || string.IsNullOrWhiteSpace(options.Model))
        {
            return WorkflowGenerationProposal.Error($"Provider '{_providerId}' is not configured.", _providerId);
        }

        var model = string.IsNullOrWhiteSpace(request.ModelOverride) ? options.Model : request.ModelOverride!;

        using var activity = HonuaTelemetry.ActivitySource.StartActivity("honua.workflowgen.generate");
        activity?.SetTag("workflowgen.provider", _providerId);
        activity?.SetTag("workflowgen.model", model);
        WorkflowGenerationLog.GenerationRequested(_logger, _providerId, model);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = _chatClientFactory.Create(
                model,
                options.Region,
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

            // Surface max_tokens truncation explicitly. Bedrock's Converse adapter maps a
            // StopReason.Max_tokens to ChatFinishReason.Length; when that happens the forced
            // tool-call's JSON arguments are cut off mid-payload, so the DeserializeProposal below
            // would fail (or silently drop fields) and flatten to the opaque generic
            // "Provider response could not be parsed." Larger workflows (e.g. vector-tiles,
            // geocoding) are the ones that overflow MaxTokens, which is exactly the #1760 symptom.
            // The OpenAI-compatible and Anthropic providers already do this (#1979); the Bedrock
            // provider was missed.
            if (response.FinishReason == ChatFinishReason.Length)
            {
                const string Reason = "Provider response was truncated (max_tokens reached); try a higher MaxTokens.";
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
        // Intentionally generic: this is a provider-boundary call to the Bedrock SDK, which
        // surfaces transport/auth/throttling failures beyond the specific types already handled
        // above; map any remaining failure to a generic proposal error instead of crashing the caller.
        catch (Exception ex)
        {
            WorkflowGenerationLog.GenerationFailed(_logger, _providerId, ex.Message);
            return WorkflowGenerationProposal.Error("Provider request failed.", _providerId, model);
        }
    }

    private static WorkflowGenerationModelProposal? DeserializeProposal(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return null;
        }

        // The Converse adapter surfaces the forced tool-call input as a string->object map; write it
        // back to JSON (AOT-safe, no reflection serialization) and deserialize into the proposal DTO.
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
