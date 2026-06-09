// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Honua.Ai.WorkflowGeneration.Models;
using Honua.Ai.WorkflowGeneration.Prompts;
using Honua.Core.Features.WorkflowPackages.Generation;
using Honua.Core.Features.WorkflowPackages.Generation.Abstractions;
using Honua.Core.Features.WorkflowPackages.Generation.Domain;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Ai.WorkflowGeneration;

/// <summary>
/// Workflow generation provider for Anthropic Claude (provider id <c>anthropic</c>). Structured
/// output is obtained by forcing a single tool call (<c>emit_workflow</c>) whose
/// <c>input_schema</c> is the constrained proposal schema; the tool input is the proposal JSON.
/// </summary>
internal sealed class AnthropicWorkflowGenerationProvider : IWorkflowGenerationProvider
{
    private const string AnthropicVersion = "2023-06-01";
    private const string ToolName = "emit_workflow";

    private readonly string _providerId;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WorkflowGenerationApiKeyResolver _apiKeyResolver;
    private readonly WorkflowGenerationConfiguration _configuration;
    private readonly ILogger<AnthropicWorkflowGenerationProvider> _logger;

    public AnthropicWorkflowGenerationProvider(
        string providerId,
        IHttpClientFactory httpClientFactory,
        WorkflowGenerationApiKeyResolver apiKeyResolver,
        IOptions<WorkflowGenerationConfiguration> options,
        ILogger<AnthropicWorkflowGenerationProvider> logger)
    {
        _providerId = providerId;
        _httpClientFactory = httpClientFactory;
        _apiKeyResolver = apiKeyResolver;
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
            var options = _configuration.GetProvider(_providerId);
            if (options is null || string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.Model))
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(options.ApiKey)
                || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                    WorkflowGenerationApiKeyResolver.EnvVarName(_providerId)));
        }
    }

    /// <inheritdoc />
    public async Task<WorkflowGenerationProposal> GenerateAsync(
        WorkflowGenerationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = _configuration.GetProvider(_providerId);
        if (options is null)
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
            var apiKey = await _apiKeyResolver.ResolveAsync(_providerId, options, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return WorkflowGenerationProposal.Error("Anthropic API key is not configured.", _providerId, model);
            }

            var messagesRequest = new AnthropicMessagesRequest
            {
                Model = model,
                MaxTokens = options.MaxTokens,
                Temperature = 0.0,
                System = WorkflowGenerationPrompt.BuildSystem(request),
                Messages =
                [
                    new AnthropicMessage { Role = "user", Content = WorkflowGenerationPrompt.BuildUser(request) }
                ],
                Tools =
                [
                    new AnthropicTool
                    {
                        Name = ToolName,
                        Description = "Emit the proposed workflow.package graph (or a clarification/refusal).",
                        InputSchema = WorkflowGenerationSchema.Build(request.Registry.Nodes)
                    }
                ],
                ToolChoice = new AnthropicToolChoice { Type = "tool", Name = ToolName }
            };

            var client = _httpClientFactory.CreateClient("workflow-generation");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

            var endpoint = options.Endpoint.TrimEnd('/') + "/v1/messages";
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
            httpRequest.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            httpRequest.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

            var requestJson = JsonSerializer.Serialize(
                messagesRequest, WorkflowGenerationJsonContext.Default.AnthropicMessagesRequest);
            httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var reason = $"Provider returned HTTP {(int)response.StatusCode}.";
                WorkflowGenerationLog.GenerationFailed(_logger, _providerId, $"{reason} {Truncate(body, 500)}");
                return WorkflowGenerationProposal.Error(reason, _providerId, model);
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var messagesResponse = JsonSerializer.Deserialize(
                responseJson, WorkflowGenerationJsonContext.Default.AnthropicMessagesResponse);

            // Find the forced tool_use block and read its input as the proposal.
            var toolInput = messagesResponse?.Content?
                .FirstOrDefault(block => string.Equals(block.Type, "tool_use", StringComparison.Ordinal)
                    && string.Equals(block.Name, ToolName, StringComparison.Ordinal))?
                .Input;

            if (toolInput is not { } input || input.ValueKind == JsonValueKind.Undefined)
            {
                const string Reason = "Provider did not return the expected tool output.";
                WorkflowGenerationLog.GenerationFailed(_logger, _providerId, Reason);
                return WorkflowGenerationProposal.Error(Reason, _providerId, model);
            }

            var proposalModel = input.Deserialize(WorkflowGenerationJsonContext.Default.WorkflowGenerationModelProposal);
            if (proposalModel is null)
            {
                const string Reason = "Failed to deserialize the workflow proposal from the tool output.";
                WorkflowGenerationLog.GenerationFailed(_logger, _providerId, Reason);
                return WorkflowGenerationProposal.Error(Reason, _providerId, model);
            }

            stopwatch.Stop();
            var usage = new WorkflowGenerationUsage
            {
                PromptTokens = messagesResponse?.Usage?.InputTokens,
                CompletionTokens = messagesResponse?.Usage?.OutputTokens,
                LatencyMs = stopwatch.ElapsedMilliseconds
            };

            var proposal = WorkflowGenerationProposalMapper.ToProposal(proposalModel, _providerId, model, usage);
            WorkflowGenerationLog.GenerationProduced(
                _logger, proposal.Status, _providerId, proposal.Graph?.Nodes.Count ?? 0);
            activity?.SetTag("workflowgen.success", true);
            activity?.SetTag("workflowgen.status", proposal.Status.ToString());
            return proposal;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            WorkflowGenerationLog.GenerationFailed(_logger, _providerId, "Request timed out.");
            return WorkflowGenerationProposal.Error("Provider request timed out.", _providerId, model);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            WorkflowGenerationLog.GenerationFailed(_logger, _providerId, ex.Message);
            return WorkflowGenerationProposal.Error("Provider request failed.", _providerId, model);
        }
        catch (JsonException ex)
        {
            WorkflowGenerationLog.GenerationFailed(_logger, _providerId, ex.Message);
            return WorkflowGenerationProposal.Error("Provider response could not be parsed.", _providerId, model);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
