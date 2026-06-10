// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Net.Http.Headers;
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
/// Workflow generation provider that calls an OpenAI-compatible <c>/v1/chat/completions</c>
/// endpoint with a strict <c>json_schema</c> response format constrained to the grounded node
/// whitelist. A single class serves two provider ids — <c>local</c> (point the endpoint at
/// Ollama/vLLM/LiteLLM hosting a GIS-tuned model) and <c>openai</c> (point it at OpenAI) — each
/// registered as a distinct instance bound to its own config block. Mirrors
/// <c>OpenAiNlQueryPlanProvider</c>.
/// </summary>
internal sealed class OpenAiCompatibleWorkflowGenerationProvider : IWorkflowGenerationProvider
{
    private readonly string _providerId;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WorkflowGenerationApiKeyResolver _apiKeyResolver;
    private readonly WorkflowGenerationConfiguration _configuration;
    private readonly ILogger<OpenAiCompatibleWorkflowGenerationProvider> _logger;

    public OpenAiCompatibleWorkflowGenerationProvider(
        string providerId,
        IHttpClientFactory httpClientFactory,
        WorkflowGenerationApiKeyResolver apiKeyResolver,
        IOptions<WorkflowGenerationConfiguration> options,
        ILogger<OpenAiCompatibleWorkflowGenerationProvider> logger)
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

            // The hosted OpenAI provider needs a key; a localhost model typically does not.
            if (string.Equals(_providerId, WorkflowGenerationConfiguration.OpenAiProviderId, StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrWhiteSpace(options.ApiKey)
                    || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                        WorkflowGenerationApiKeyResolver.EnvVarName(_providerId)));
            }

            return true;
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

            var chatRequest = new OpenAiChatCompletionRequest
            {
                Model = model,
                MaxTokens = options.MaxTokens,
                Temperature = 0.0,
                Messages =
                [
                    new OpenAiMessage { Role = "system", Content = WorkflowGenerationPrompt.BuildSystem(request) },
                    new OpenAiMessage { Role = "user", Content = WorkflowGenerationPrompt.BuildUser(request) }
                ],
                ResponseFormat = new OpenAiResponseFormat
                {
                    Type = "json_schema",
                    JsonSchema = new OpenAiJsonSchema
                    {
                        Name = "workflow_proposal",
                        Strict = true,
                        Schema = WorkflowGenerationSchema.Build(request.Registry.Nodes)
                    }
                }
            };

            var client = _httpClientFactory.CreateClient("workflow-generation");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

            var endpoint = options.Endpoint.TrimEnd('/') + "/chat/completions";
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var requestJson = JsonSerializer.Serialize(
                chatRequest, WorkflowGenerationJsonContext.Default.OpenAiChatCompletionRequest);
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
            var chatResponse = JsonSerializer.Deserialize(
                responseJson, WorkflowGenerationJsonContext.Default.OpenAiChatCompletionResponse);

            var content = chatResponse?.Choices is { Length: > 0 } ? chatResponse.Choices[0].Message?.Content : null;
            if (string.IsNullOrWhiteSpace(content))
            {
                const string Reason = "Provider returned an empty response.";
                WorkflowGenerationLog.GenerationFailed(_logger, _providerId, Reason);
                return WorkflowGenerationProposal.Error(Reason, _providerId, model);
            }

            var proposalModel = JsonSerializer.Deserialize(
                content, WorkflowGenerationJsonContext.Default.WorkflowGenerationModelProposal);
            if (proposalModel is null)
            {
                const string Reason = "Failed to deserialize the workflow proposal from the provider response.";
                WorkflowGenerationLog.GenerationFailed(_logger, _providerId, Reason);
                return WorkflowGenerationProposal.Error(Reason, _providerId, model);
            }

            stopwatch.Stop();
            var usage = new WorkflowGenerationUsage
            {
                PromptTokens = chatResponse?.Usage?.PromptTokens,
                CompletionTokens = chatResponse?.Usage?.CompletionTokens,
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
