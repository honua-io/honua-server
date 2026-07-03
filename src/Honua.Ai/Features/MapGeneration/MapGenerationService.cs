// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Honua.Ai.WorkflowGeneration;
using Honua.Ai.WorkflowGeneration.Models;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.WorkflowPackages.Generation;
using Microsoft.Extensions.Options;

namespace Honua.Ai.MapGeneration;

/// <summary>
/// Default <see cref="IMapGenerationService"/>: grounds a prompt in the map-package vocabulary (source
/// protocols, basemaps, style fields), calls an OpenAI-compatible provider (local/openai) with a strict
/// json_schema, and applies <see cref="MapGenerationStructuralValidator"/> as a generation-lenient gate
/// (structural failures only; layer/style/source binding deferred to publish) with a bounded repair
/// loop. Reuses the workflow-generation provider configuration + chat plumbing so a single local model
/// serves the workflow, form, and map families.
/// </summary>
public sealed class MapGenerationService : IMapGenerationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WorkflowGenerationConfiguration _configuration;
    private readonly WorkflowGenerationApiKeyResolver _apiKeyResolver;
    private readonly ILogger<MapGenerationService> _logger;

    public MapGenerationService(
        IHttpClientFactory httpClientFactory,
        IOptions<WorkflowGenerationConfiguration> options,
        WorkflowGenerationApiKeyResolver apiKeyResolver,
        ILogger<MapGenerationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = options.Value;
        _apiKeyResolver = apiKeyResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MapGenerationResult> GenerateAsync(MapGenerationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_configuration.Enabled)
        {
            return Unsupported("AI map generation is disabled on this server.");
        }

        var providerId = string.IsNullOrWhiteSpace(request.Provider) ? _configuration.DefaultProvider : request.Provider!;
        var options = _configuration.GetProvider(providerId);
        if (options is null || string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.Model))
        {
            return Unsupported($"Map generation provider '{providerId}' is not configured on this server.");
        }

        var model = string.IsNullOrWhiteSpace(request.Model) ? options.Model! : request.Model!;

        var providerRequest = new MapGenerationProviderRequest
        {
            Prompt = request.Prompt,
            ModelOverride = request.Model,
            Conversation = request.Conversation,
            Answers = request.Answers,
            CurrentMap = request.CurrentMap
        };

        var proposal = await CallModelAsync(providerRequest, options, providerId, model, cancellationToken).ConfigureAwait(false);

        // The server owns format/status/createdAt; never depend on the model for them. Force canonical
        // values before validating + returning.
        var map = Normalize(proposal.Map);

        MapPackageValidationResult? validation = null;
        if (string.Equals(proposal.Status, "generated", StringComparison.Ordinal) && map is not null)
        {
            validation = MapGenerationStructuralValidator.Validate(map);
            var gate = MapGenerationValidationGate.Evaluate(validation);

            var attempts = 0;
            while (!gate.Passed && attempts < _configuration.MaxRepairAttempts)
            {
                attempts++;
                var repair = providerRequest with { RepairFailures = gate.StructuralFailures };
                proposal = await CallModelAsync(repair, options, providerId, model, cancellationToken).ConfigureAwait(false);
                map = Normalize(proposal.Map);
                if (!string.Equals(proposal.Status, "generated", StringComparison.Ordinal) || map is null)
                {
                    break;
                }

                validation = MapGenerationStructuralValidator.Validate(map);
                gate = MapGenerationValidationGate.Evaluate(validation);
            }

            if (string.Equals(proposal.Status, "generated", StringComparison.Ordinal) && map is not null && !gate.Passed)
            {
                var summary = string.Join("; ", gate.StructuralFailures.Select(f => f.Message));
                return new MapGenerationResult
                {
                    Status = "error",
                    Rationale = "The proposed map did not pass server validation: " + summary,
                    Validation = validation,
                    Provider = providerId,
                    Model = model
                };
            }
        }

        return new MapGenerationResult
        {
            Status = proposal.Status,
            Package = string.Equals(proposal.Status, "generated", StringComparison.Ordinal) ? map : null,
            Rationale = proposal.Rationale,
            Clarifications = MapClarifications(proposal.Clarifications),
            Validation = validation,
            UnmappedRequests = proposal.UnmappedRequests,
            CapabilityState = proposal.CapabilityState is null
                ? null
                : new MapGenerationCapabilityState
                {
                    Name = proposal.CapabilityState.Name,
                    State = proposal.CapabilityState.State,
                    Reason = proposal.CapabilityState.Reason
                },
            Provider = providerId,
            Model = model
        };
    }

    private async Task<MapGenerationModelProposal> CallModelAsync(
        MapGenerationProviderRequest request,
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
                    new OpenAiMessage { Role = "system", Content = MapGenerationPrompt.BuildSystem(request) },
                    new OpenAiMessage { Role = "user", Content = MapGenerationPrompt.BuildUser(request) }
                ],
                ResponseFormat = new OpenAiResponseFormat
                {
                    Type = "json_schema",
                    JsonSchema = new OpenAiJsonSchema
                    {
                        Name = "map_proposal",
                        Strict = true,
                        Schema = MapGenerationSchema.Build()
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

            var proposal = JsonSerializer.Deserialize(content, MapGenerationJsonContext.Default.MapGenerationModelProposal);
            return proposal ?? ErrorProposal("Failed to deserialize the map proposal from the provider response.");
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

    private static MapGenerationResult Unsupported(string reason) => new()
    {
        Status = "unsupported",
        Rationale = reason
    };

    private static MapGenerationModelProposal ErrorProposal(string reason) => new()
    {
        Status = "error",
        Rationale = reason
    };

    private const string MapPackageFormat = "honua_map_package.v1";

    /// <summary>
    /// Returns the map with the server-canonical format and a server-owned Draft status/timestamp
    /// (MapPackage is immutable). The model may emit a wrong format or status despite the schema.
    /// </summary>
    private static MapPackage? Normalize(MapPackage? map)
    {
        if (map is null)
        {
            return null;
        }

        if (string.Equals(map.Format, MapPackageFormat, StringComparison.Ordinal) && map.Status == PackageStatus.Draft)
        {
            return map;
        }

        return map with
        {
            Format = MapPackageFormat,
            Status = PackageStatus.Draft
        };
    }

    private static MapGenerationClarification[] MapClarifications(MapGenerationModelClarification[] clarifications) =>
        clarifications
            .Select(c => new MapGenerationClarification
            {
                Id = c.Id,
                Kind = c.Kind,
                Prompt = c.Prompt,
                Reason = c.Reason,
                Choices = c.Choices
                    .Select(choice => new MapGenerationClarificationChoice { Id = choice.Id, Label = choice.Label, Effect = choice.Effect })
                    .ToArray()
            })
            .ToArray();
}
