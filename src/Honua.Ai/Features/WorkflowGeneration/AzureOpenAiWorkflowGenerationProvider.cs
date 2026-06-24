// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using Honua.Ai.Providers.AzureOpenAi;
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
/// Workflow generation provider for Azure OpenAI / Azure AI Foundry (provider id
/// <c>azureopenai</c>). Azure OpenAI is OpenAI-API-compatible, so structured output is obtained the
/// same way as the OpenAI-compatible provider — a strict <c>json_schema</c> response format
/// constrained to the grounded node whitelist — over the deployment-routed Azure URL
/// (<c>{endpoint}/openai/deployments/{deployment}/chat/completions?api-version=…</c>). Auth uses
/// Entra Managed Identity (preferred) or an <c>api-key</c>. This lets the workflow studio flow run
/// on Azure-hosted AI for Bedrock parity, without a local Ollama/Qwen model.
/// </summary>
internal sealed class AzureOpenAiWorkflowGenerationProvider : IWorkflowGenerationProvider
{
    private readonly string _providerId;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AzureOpenAiAuthResolver _authResolver;
    private readonly WorkflowGenerationConfiguration _configuration;
    private readonly ILogger<AzureOpenAiWorkflowGenerationProvider> _logger;

    public AzureOpenAiWorkflowGenerationProvider(
        string providerId,
        IHttpClientFactory httpClientFactory,
        AzureOpenAiAuthResolver authResolver,
        IOptions<WorkflowGenerationConfiguration> options,
        ILogger<AzureOpenAiWorkflowGenerationProvider> logger)
    {
        _providerId = providerId;
        _httpClientFactory = httpClientFactory;
        _authResolver = authResolver;
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
            // Azure needs a resource endpoint and a deployment (or model) to route by. Auth is via
            // Managed Identity (preferred) or a key, so the key is not required to be "configured".
            var options = _configuration.GetProvider(_providerId);
            return options is not null
                && !string.IsNullOrWhiteSpace(options.Endpoint)
                && (!string.IsNullOrWhiteSpace(options.Deployment) || !string.IsNullOrWhiteSpace(options.Model));
        }
    }

    /// <inheritdoc />
    public async Task<WorkflowGenerationProposal> GenerateAsync(
        WorkflowGenerationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = _configuration.GetProvider(_providerId);
        if (options is null || string.IsNullOrWhiteSpace(options.Endpoint))
        {
            return WorkflowGenerationProposal.Error($"Provider '{_providerId}' is not configured.", _providerId);
        }

        var model = string.IsNullOrWhiteSpace(request.ModelOverride) ? options.Model : request.ModelOverride!;
        if (string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(options.Deployment))
        {
            return WorkflowGenerationProposal.Error($"Provider '{_providerId}' is not configured.", _providerId);
        }

        // The effective model id used for telemetry/usage; the Azure URL routes by deployment.
        var effectiveModel = string.IsNullOrWhiteSpace(model) ? options.Deployment : model;

        using var activity = HonuaTelemetry.ActivitySource.StartActivity("honua.workflowgen.generate");
        activity?.SetTag("workflowgen.provider", _providerId);
        activity?.SetTag("workflowgen.model", effectiveModel);
        WorkflowGenerationLog.GenerationRequested(_logger, _providerId, effectiveModel);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var credential = await _authResolver.ResolveAsync(_providerId, options, cancellationToken).ConfigureAwait(false);

            var client = _httpClientFactory.CreateClient("workflow-generation");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

            var result = await AzureOpenAiStructuredGenerationClient.GenerateAsync(
                client,
                options,
                effectiveModel,
                credential.ApiKey,
                credential.AccessToken,
                WorkflowGenerationPrompt.BuildSystem(request),
                WorkflowGenerationPrompt.BuildUser(request),
                "workflow_proposal",
                WorkflowGenerationSchema.Build(request.Registry.Nodes),
                cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                WorkflowGenerationLog.GenerationFailed(_logger, _providerId, result.Error ?? "Azure OpenAI request failed.");
                return WorkflowGenerationProposal.Error(result.Error ?? "Provider request failed.", _providerId, effectiveModel);
            }

            var proposalModel = JsonSerializer.Deserialize(
                result.Json!, WorkflowGenerationJsonContext.Default.WorkflowGenerationModelProposal);
            if (proposalModel is null)
            {
                const string Reason = "Failed to deserialize the workflow proposal from the provider response.";
                WorkflowGenerationLog.GenerationFailed(_logger, _providerId, Reason);
                return WorkflowGenerationProposal.Error(Reason, _providerId, effectiveModel);
            }

            stopwatch.Stop();
            var usage = new WorkflowGenerationUsage
            {
                LatencyMs = stopwatch.ElapsedMilliseconds
            };

            var proposal = WorkflowGenerationProposalMapper.ToProposal(proposalModel, _providerId, effectiveModel, usage);
            WorkflowGenerationLog.GenerationProduced(
                _logger, proposal.Status, _providerId, proposal.Graph?.Nodes.Count ?? 0);
            activity?.SetTag("workflowgen.success", true);
            activity?.SetTag("workflowgen.status", proposal.Status.ToString());
            return proposal;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            WorkflowGenerationLog.GenerationFailed(_logger, _providerId, "Request timed out.");
            return WorkflowGenerationProposal.Error("Provider request timed out.", _providerId, effectiveModel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            WorkflowGenerationLog.GenerationFailed(_logger, _providerId, ex.Message);
            return WorkflowGenerationProposal.Error("Provider request failed.", _providerId, effectiveModel);
        }
        catch (JsonException ex)
        {
            WorkflowGenerationLog.GenerationFailed(_logger, _providerId, ex.Message);
            return WorkflowGenerationProposal.Error("Provider response could not be parsed.", _providerId, effectiveModel);
        }
    }
}
