// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

    public MapGenerationService(
        IHttpClientFactory httpClientFactory,
        IOptions<WorkflowGenerationConfiguration> options)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = options.Value;
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
            CurrentMap = request.CurrentMap,
            AvailableSources = request.AvailableSources
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
            // A localhost model typically needs no key; the hosted provider reads it from config or env.
            var apiKey = string.IsNullOrWhiteSpace(options.ApiKey)
                ? Environment.GetEnvironmentVariable($"HONUA_WORKFLOWGEN_{providerId.ToUpperInvariant()}_API_KEY")
                : options.ApiKey;

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
                _ = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ErrorProposal($"Provider returned HTTP {(int)response.StatusCode}.");
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var chatResponse = JsonSerializer.Deserialize(responseJson, WorkflowGenerationJsonContext.Default.OpenAiChatCompletionResponse);
            var content = chatResponse?.Choices is { Length: > 0 } ? chatResponse.Choices[0].Message?.Content : null;
            if (string.IsNullOrWhiteSpace(content))
            {
                return ErrorProposal("Provider returned an empty response.");
            }

            // The server owns map.createdAt / map.status / map.format (Normalize forces canonical values),
            // so a small local model emitting a non-canonical value for any of them must not fail the parse.
            // Strip those server-owned fields from the model JSON before deserializing; they default and are
            // then overwritten. Best-effort: if sanitization fails, deserialize the original content.
            var sanitized = SanitizeServerOwnedMapFields(content);
            var proposal = JsonSerializer.Deserialize(sanitized, MapGenerationJsonContext.Default.MapGenerationModelProposal);
            return proposal ?? ErrorProposal("Failed to deserialize the map proposal from the provider response.");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            return ErrorProposal("Provider request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return ErrorProposal($"HTTP request failed: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return ErrorProposal($"Failed to parse provider response: {ex.Message}");
        }
    }

    // The server owns map.createdAt (Normalize forces format/status, but createdAt is a required typed
    // DateTimeOffset). A small local model often emits a non-parseable createdAt (e.g. a date-only or
    // templated value), which fails the strict deserialize before the server can override it. Replace it
    // with a valid placeholder so the parse succeeds; the package's real timestamp is set server-side.
    // Best-effort: returns the original content on any failure so the caller surfaces a clear error.
    private static string SanitizeServerOwnedMapFields(string content)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(content);
            if (node is System.Text.Json.Nodes.JsonObject root
                && root.TryGetPropertyValue("map", out var mapNode)
                && mapNode is System.Text.Json.Nodes.JsonObject map)
            {
                map["createdAt"] = "2000-01-01T00:00:00+00:00";
                return root.ToJsonString();
            }
        }
        catch (JsonException)
        {
            // Fall through to the original content; the caller's deserialize will surface a clear error.
        }

        return content;
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
