// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text.Json;
using Honua.Core.Features.NlQuery;
using Honua.Core.Features.NlQuery.Abstractions;
using Honua.Core.Features.NlQuery.Domain;
using Honua.Server.Features.NlQuery.Models;
using Honua.Server.Features.NlQuery.Prompts;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.NlQuery;

/// <summary>
/// NL query plan provider that calls an OpenAI-compatible chat completions endpoint.
/// Requests structured JSON output constrained to the <see cref="FilterPlan"/> schema.
/// </summary>
internal sealed class OpenAiNlQueryPlanProvider : INlQueryPlanProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiNlQueryPlanProvider> _logger;
    private readonly NlQueryConfiguration _configuration;

    /// <summary>
    /// Initializes the OpenAI-compatible NL query plan provider.
    /// </summary>
    public OpenAiNlQueryPlanProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<NlQueryConfiguration> options,
        ILogger<OpenAiNlQueryPlanProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _configuration = options.Value;
    }

    /// <inheritdoc />
    public async Task<NlQueryPlanResult> GeneratePlanAsync(
        NlQueryPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("honua.nlquery.generate-plan");
        activity?.SetTag("nl.provider", "openai");
        activity?.SetTag("nl.model", _configuration.Model);
        activity?.SetTag("nl.collection", request.CollectionId);

        NlQueryLog.PlanRequested(_logger, request.CollectionId ?? "unknown", _configuration.Model);

        try
        {
            var systemPrompt = NlQuerySystemPrompt.Build(request.Layer);
            var chatRequest = BuildChatRequest(systemPrompt, request.Query);

            var client = _httpClientFactory.CreateClient("nl-query");
            client.Timeout = TimeSpan.FromSeconds(_configuration.TimeoutSeconds);

            var endpoint = _configuration.Endpoint.TrimEnd('/') + "/chat/completions";

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _configuration.ApiKey);

            var requestJson = JsonSerializer.Serialize(chatRequest, NlQueryJsonContext.Default.OpenAiChatCompletionRequest);
            httpRequest.Content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                NlQueryLog.PlanFailed(_logger, request.CollectionId ?? "unknown",
                    $"HTTP {(int)response.StatusCode}: {errorBody}");

                activity?.SetTag("nl.success", false);
                return NlQueryPlanResult.Failure($"Provider returned HTTP {(int)response.StatusCode}.");
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var chatResponse = JsonSerializer.Deserialize(responseJson, NlQueryJsonContext.Default.OpenAiChatCompletionResponse);

            if (chatResponse?.Choices is not { Length: > 0 } || chatResponse.Choices[0].Message?.Content is not { } content)
            {
                NlQueryLog.PlanFailed(_logger, request.CollectionId ?? "unknown", "Empty response from provider.");
                activity?.SetTag("nl.success", false);
                return NlQueryPlanResult.Failure("Provider returned an empty response.");
            }

            var plan = JsonSerializer.Deserialize(content, NlQueryJsonContext.Default.FilterPlan);
            if (plan is null)
            {
                NlQueryLog.PlanFailed(_logger, request.CollectionId ?? "unknown", "Failed to deserialize filter plan.");
                activity?.SetTag("nl.success", false);
                return NlQueryPlanResult.Failure("Failed to deserialize filter plan from provider response.");
            }

            NlQueryLog.PlanSucceeded(_logger, request.CollectionId ?? "unknown", plan.Clauses.Length);
            activity?.SetTag("nl.success", true);
            activity?.SetTag("nl.clause_count", plan.Clauses.Length);

            return NlQueryPlanResult.Success(plan);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            NlQueryLog.PlanFailed(_logger, request.CollectionId ?? "unknown", "Request timed out.");
            activity?.SetTag("nl.success", false);
            return NlQueryPlanResult.Failure("Provider request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            NlQueryLog.PlanFailed(_logger, request.CollectionId ?? "unknown", ex.Message);
            activity?.SetTag("nl.success", false);
            return NlQueryPlanResult.Failure($"HTTP request failed: {ex.Message}");
        }
        catch (JsonException ex)
        {
            NlQueryLog.PlanFailed(_logger, request.CollectionId ?? "unknown", ex.Message);
            activity?.SetTag("nl.success", false);
            return NlQueryPlanResult.Failure($"Failed to parse provider response: {ex.Message}");
        }
    }

    private OpenAiChatCompletionRequest BuildChatRequest(string systemPrompt, string userQuery)
    {
        return new OpenAiChatCompletionRequest
        {
            Model = _configuration.Model,
            MaxTokens = _configuration.MaxTokens,
            Temperature = 0.0,
            Messages =
            [
                new OpenAiMessage { Role = "system", Content = systemPrompt },
                new OpenAiMessage { Role = "user", Content = userQuery }
            ],
            ResponseFormat = new OpenAiResponseFormat
            {
                Type = "json_schema",
                JsonSchema = new OpenAiJsonSchema
                {
                    Name = "filter_plan",
                    Strict = true,
                    Schema = SerializeSchemaToElement(BuildFilterPlanJsonSchema())
                }
            }
        };
    }

    private static Dictionary<string, object> BuildFilterPlanJsonSchema()
    {
        // Minimal JSON Schema for the FilterPlan type, used by OpenAI structured output.
        // This constrains the model to only produce valid filter plan shapes.
        return new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["combinator"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["enum"] = new[] { "and", "or" }
                },
                ["clauses"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["type"] = new Dictionary<string, object>
                            {
                                ["type"] = "string",
                                ["enum"] = new[] { "comparison", "spatial", "temporal", "nested" }
                            },
                            ["comparison"] = new Dictionary<string, object>
                            {
                                ["type"] = "object",
                                ["properties"] = new Dictionary<string, object>
                                {
                                    ["property"] = new Dictionary<string, string> { ["type"] = "string" },
                                    ["operator"] = new Dictionary<string, object>
                                    {
                                        ["type"] = "string",
                                        ["enum"] = new[] { "eq", "neq", "lt", "lte", "gt", "gte", "like", "in" }
                                    },
                                    ["value"] = new Dictionary<string, object>
                                    {
                                        ["anyOf"] = new object[]
                                        {
                                            new Dictionary<string, string> { ["type"] = "string" },
                                            new Dictionary<string, string> { ["type"] = "number" },
                                            new Dictionary<string, string> { ["type"] = "boolean" },
                                            new Dictionary<string, object>
                                            {
                                                ["type"] = "array",
                                                ["items"] = new Dictionary<string, string> { ["type"] = "string" }
                                            }
                                        }
                                    }
                                },
                                ["required"] = new[] { "property", "operator", "value" },
                                ["additionalProperties"] = false
                            },
                            ["spatial"] = new Dictionary<string, object>
                            {
                                ["type"] = "object",
                                ["properties"] = new Dictionary<string, object>
                                {
                                    ["operator"] = new Dictionary<string, object>
                                    {
                                        ["type"] = "string",
                                        ["enum"] = new[] { "intersects", "within", "contains", "dwithin" }
                                    },
                                    ["geometry"] = new Dictionary<string, string> { ["type"] = "object" },
                                    ["distance"] = new Dictionary<string, string> { ["type"] = "number" },
                                    ["distanceUnit"] = new Dictionary<string, string> { ["type"] = "string" }
                                },
                                ["required"] = new[] { "operator", "geometry" },
                                ["additionalProperties"] = false
                            },
                            ["temporal"] = new Dictionary<string, object>
                            {
                                ["type"] = "object",
                                ["properties"] = new Dictionary<string, object>
                                {
                                    ["property"] = new Dictionary<string, string> { ["type"] = "string" },
                                    ["operator"] = new Dictionary<string, object>
                                    {
                                        ["type"] = "string",
                                        ["enum"] = new[] { "before", "after", "during" }
                                    },
                                    ["start"] = new Dictionary<string, string> { ["type"] = "string" },
                                    ["end"] = new Dictionary<string, string> { ["type"] = "string" }
                                },
                                ["required"] = new[] { "property", "operator" },
                                ["additionalProperties"] = false
                            },
                            ["nested"] = new Dictionary<string, object>
                            {
                                ["type"] = "object",
                                ["properties"] = new Dictionary<string, object>
                                {
                                    ["combinator"] = new Dictionary<string, object>
                                    {
                                        ["type"] = "string",
                                        ["enum"] = new[] { "and", "or" }
                                    },
                                    ["clauses"] = new Dictionary<string, object>
                                    {
                                        ["type"] = "array",
                                        ["items"] = new Dictionary<string, string> { ["type"] = "object" }
                                    }
                                },
                                ["required"] = new[] { "combinator", "clauses" },
                                ["additionalProperties"] = false
                            }
                        },
                        ["required"] = new[] { "type" },
                        ["additionalProperties"] = false
                    }
                }
            },
            ["required"] = new[] { "combinator", "clauses" },
            ["additionalProperties"] = false
        };
    }

    private static JsonElement SerializeSchemaToElement(Dictionary<string, object> schema)
    {
        var json = JsonSerializer.Serialize(schema);
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
