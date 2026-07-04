// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Honua.Ai.WorkflowGeneration;
using Honua.Ai.WorkflowGeneration.Models;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.WorkflowPackages.Generation;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Honua.Ai.AnalysisGeneration;

/// <summary>
/// Default <see cref="IAnalysisGenerationService"/>: grounds a prompt in the geoprocessing
/// analysis-method vocabulary (the process catalog), calls an OpenAI-compatible provider (local/openai)
/// with a strict json_schema, and applies the structural <c>ProcessPlanValidator</c> as a
/// generation-lenient gate (method/parameter structure only; input layer existence deferred to
/// run/publish) with a bounded repair loop. Reuses the workflow-generation provider configuration +
/// chat plumbing so a single local model serves form, workflow, and analysis families.
/// </summary>
public sealed class AnalysisGenerationService : IAnalysisGenerationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WorkflowGenerationConfiguration _configuration;
    private readonly IProcessCatalog _processCatalog;
    private readonly WorkflowGenerationApiKeyResolver _apiKeyResolver;
    private readonly ILogger<AnalysisGenerationService> _logger;

    public AnalysisGenerationService(
        IHttpClientFactory httpClientFactory,
        IOptions<WorkflowGenerationConfiguration> options,
        IProcessCatalog processCatalog,
        WorkflowGenerationApiKeyResolver apiKeyResolver,
        ILogger<AnalysisGenerationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = options.Value;
        _processCatalog = processCatalog;
        _apiKeyResolver = apiKeyResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AnalysisGenerationResult> GenerateAsync(AnalysisGenerationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = HonuaTelemetry.ActivitySource.StartActivity("honua.analysis_generation.generate");

        if (!_configuration.Enabled)
        {
            return Unsupported("AI analysis generation is disabled on this server.");
        }

        var providerId = string.IsNullOrWhiteSpace(request.Provider) ? _configuration.DefaultProvider : request.Provider!;
        activity?.SetTag("honua.ai.provider", providerId);

        var options = _configuration.GetProvider(providerId);
        if (options is null || string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.Model))
        {
            return Unsupported($"Analysis generation provider '{providerId}' is not configured on this server.");
        }

        var model = string.IsNullOrWhiteSpace(request.Model) ? options.Model! : request.Model!;

        var providerRequest = new AnalysisGenerationProviderRequest
        {
            Prompt = request.Prompt,
            ModelOverride = request.Model,
            Conversation = request.Conversation,
            Answers = request.Answers,
            CurrentAnalysis = request.CurrentAnalysis
        };

        var proposal = await CallModelAsync(providerRequest, options, providerId, model, cancellationToken).ConfigureAwait(false);

        var analysis = proposal.Analysis;
        AnalysisGenerationValidation? validation = null;
        if (string.Equals(proposal.Status, "generated", StringComparison.Ordinal) && analysis is not null)
        {
            var gate = AnalysisGenerationValidationGate.Evaluate(analysis.Plan, _processCatalog);
            validation = AnalysisGenerationValidationGate.ToValidation(gate);

            var attempts = 0;
            while (!gate.Passed && attempts < _configuration.MaxRepairAttempts)
            {
                attempts++;
                var repair = providerRequest with { RepairFailures = gate.StructuralFailures };
                proposal = await CallModelAsync(repair, options, providerId, model, cancellationToken).ConfigureAwait(false);
                analysis = proposal.Analysis;
                if (!string.Equals(proposal.Status, "generated", StringComparison.Ordinal) || analysis is null)
                {
                    break;
                }

                gate = AnalysisGenerationValidationGate.Evaluate(analysis.Plan, _processCatalog);
                validation = AnalysisGenerationValidationGate.ToValidation(gate);
            }

            if (string.Equals(proposal.Status, "generated", StringComparison.Ordinal) && analysis is not null && !gate.Passed)
            {
                var summary = string.Join("; ", gate.StructuralFailures.Select(f => f.Message));
                return new AnalysisGenerationResult
                {
                    Status = "error",
                    Rationale = "The proposed analysis did not pass server validation: " + summary,
                    Validation = validation,
                    Provider = providerId,
                    Model = model
                };
            }
        }

        // A "generated" status with no analysis/plan is malformed model output, not a real plan.
        if (string.Equals(proposal.Status, "generated", StringComparison.Ordinal) && analysis is null)
        {
            return new AnalysisGenerationResult
            {
                Status = "error",
                Rationale = "The provider reported a generated analysis but returned no analysis package.",
                Provider = providerId,
                Model = model
            };
        }

        return new AnalysisGenerationResult
        {
            Status = proposal.Status,
            Analysis = string.Equals(proposal.Status, "generated", StringComparison.Ordinal) ? analysis : null,
            Rationale = proposal.Rationale,
            Clarifications = MapClarifications(proposal.Clarifications),
            Validation = validation,
            UnmappedRequests = proposal.UnmappedRequests,
            CapabilityState = proposal.CapabilityState is null
                ? null
                : new AnalysisGenerationCapabilityState
                {
                    Name = proposal.CapabilityState.Name,
                    State = proposal.CapabilityState.State,
                    Reason = proposal.CapabilityState.Reason
                },
            Provider = providerId,
            Model = model
        };
    }

    private async Task<AnalysisGenerationModelProposal> CallModelAsync(
        AnalysisGenerationProviderRequest request,
        WorkflowGenerationProviderOptions options,
        string providerId,
        string model,
        CancellationToken cancellationToken)
    {
        try
        {
            var apiKey = await _apiKeyResolver.ResolveAsync(providerId, options, cancellationToken).ConfigureAwait(false);

            var chatRequest = new OpenAiChatCompletionRequest
            {
                Model = model,
                MaxTokens = options.MaxTokens,
                Temperature = 0.0,
                Messages =
                [
                    new OpenAiMessage { Role = "system", Content = AnalysisGenerationPrompt.BuildSystem(request, _processCatalog) },
                    new OpenAiMessage { Role = "user", Content = AnalysisGenerationPrompt.BuildUser(request) }
                ],
                ResponseFormat = new OpenAiResponseFormat
                {
                    Type = "json_schema",
                    JsonSchema = new OpenAiJsonSchema
                    {
                        Name = "analysis_proposal",
                        Strict = true,
                        Schema = AnalysisGenerationSchema.Build(_processCatalog)
                    }
                }
            };

            var client = _httpClientFactory.CreateClient("workflow-generation");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

            var endpoint = options.Endpoint!.TrimEnd('/') + "/chat/completions";
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var requestJson = JsonSerializer.Serialize(chatRequest, WorkflowGenerationJsonContext.Default.OpenAiChatCompletionRequest);
            httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                GenerationProviderLog.ProviderHttpError(_logger, providerId, status, GenerationStringHelpers.Truncate(errorBody));
                return ErrorProposal($"Provider returned HTTP {status}.");
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var chatResponse = JsonSerializer.Deserialize(responseJson, WorkflowGenerationJsonContext.Default.OpenAiChatCompletionResponse);
            // Guard a null first element ("choices":[null]) — length check is not a null check (#1986).
            var choice = chatResponse?.Choices is { Length: > 0 } choices ? choices[0] : null;

            // Surface max_tokens truncation explicitly instead of failing later with the opaque
            // generic deserialize error on a truncated JSON body (#1979).
            if (string.Equals(choice?.FinishReason, "length", StringComparison.Ordinal))
            {
                return ErrorProposal("Provider response was truncated (finish_reason=length / max_tokens reached); try a higher MaxTokens.");
            }

            var content = choice?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                return ErrorProposal("Provider returned an empty response.");
            }

            var proposal = JsonSerializer.Deserialize(content, AnalysisGenerationJsonContext.Default.AnalysisGenerationModelProposal);
            return proposal ?? ErrorProposal("Failed to deserialize the analysis proposal from the provider response.");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            GenerationProviderLog.ProviderTimeout(_logger, providerId);
            return ErrorProposal("Provider request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            GenerationProviderLog.ProviderRequestFailed(_logger, providerId, ex);
            return ErrorProposal("Provider request failed.");
        }
        catch (JsonException ex)
        {
            GenerationProviderLog.ProviderResponseParseFailed(_logger, providerId, ex);
            return ErrorProposal("Provider response could not be parsed.");
        }
    }

    private static AnalysisGenerationResult Unsupported(string reason) => new()
    {
        Status = "unsupported",
        Rationale = reason
    };

    private static AnalysisGenerationModelProposal ErrorProposal(string reason) => new()
    {
        Status = "error",
        Rationale = reason
    };

    private static AnalysisGenerationClarification[] MapClarifications(AnalysisGenerationModelClarification[] clarifications) =>
        clarifications
            .Select(c => new AnalysisGenerationClarification
            {
                Id = c.Id,
                Kind = c.Kind,
                Prompt = c.Prompt,
                Reason = c.Reason,
                Choices = c.Choices
                    .Select(choice => new AnalysisGenerationClarificationChoice { Id = choice.Id, Label = choice.Label, Effect = choice.Effect })
                    .ToArray()
            })
            .ToArray();
}
