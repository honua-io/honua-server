// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Honua.Ai.WorkflowGeneration.Models;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.WorkflowPackages.Generation;
using Microsoft.Extensions.Options;

namespace Honua.Ai.QueryGeneration;

/// <summary>
/// Default <see cref="IQueryGenerationService"/>: grounds a prompt in the saved-query (spatial/attribute
/// filter) vocabulary, calls an OpenAI-compatible provider (local/openai) with a strict json_schema, and
/// applies the structural <see cref="QueryGenerationValidationGate"/> as a generation-lenient gate
/// (filter-plan structure only; target layer + field-schema binding deferred to run/preview) with a
/// bounded repair loop. Reuses the workflow-generation provider configuration + chat plumbing so a single
/// local model serves the form, workflow, analysis, and query families.
/// </summary>
public sealed class QueryGenerationService : IQueryGenerationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WorkflowGenerationConfiguration _configuration;

    public QueryGenerationService(
        IHttpClientFactory httpClientFactory,
        IOptions<WorkflowGenerationConfiguration> options)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = options.Value;
    }

    /// <inheritdoc />
    public async Task<QueryGenerationResult> GenerateAsync(QueryGenerationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_configuration.Enabled)
        {
            return Unsupported("AI query generation is disabled on this server.");
        }

        var providerId = string.IsNullOrWhiteSpace(request.Provider) ? _configuration.DefaultProvider : request.Provider!;
        var options = _configuration.GetProvider(providerId);
        if (options is null || string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.Model))
        {
            return Unsupported($"Query generation provider '{providerId}' is not configured on this server.");
        }

        var model = string.IsNullOrWhiteSpace(request.Model) ? options.Model! : request.Model!;

        var providerRequest = new QueryGenerationProviderRequest
        {
            Prompt = request.Prompt,
            ModelOverride = request.Model,
            Conversation = request.Conversation,
            Answers = request.Answers,
            CurrentQuery = request.CurrentQuery
        };

        var proposal = await CallModelAsync(providerRequest, options, providerId, model, cancellationToken).ConfigureAwait(false);

        // The server owns the echoed natural-language query text; never depend on the model to round-trip
        // it faithfully (a local model may paraphrase or drop it). Force the caller's prompt back onto the
        // saved query before validating + returning (the form NormalizeSchemaVersion lesson applied here).
        var query = NormalizeServerOwnedFields(proposal.Query, request.Prompt);

        QueryGenerationValidation? validation = null;
        if (string.Equals(proposal.Status, "generated", StringComparison.Ordinal) && query is not null)
        {
            var gate = QueryGenerationValidationGate.Evaluate(query);
            validation = QueryGenerationValidationGate.ToValidation(gate);

            var attempts = 0;
            while (!gate.Passed && attempts < _configuration.MaxRepairAttempts)
            {
                attempts++;
                var repair = providerRequest with { RepairFailures = gate.StructuralFailures };
                proposal = await CallModelAsync(repair, options, providerId, model, cancellationToken).ConfigureAwait(false);
                query = NormalizeServerOwnedFields(proposal.Query, request.Prompt);
                if (!string.Equals(proposal.Status, "generated", StringComparison.Ordinal) || query is null)
                {
                    break;
                }

                gate = QueryGenerationValidationGate.Evaluate(query);
                validation = QueryGenerationValidationGate.ToValidation(gate);
            }

            if (string.Equals(proposal.Status, "generated", StringComparison.Ordinal) && query is not null && !gate.Passed)
            {
                var summary = string.Join("; ", gate.StructuralFailures.Select(f => f.Message));
                return new QueryGenerationResult
                {
                    Status = "error",
                    Rationale = "The proposed query did not pass server validation: " + summary,
                    Validation = validation,
                    Provider = providerId,
                    Model = model
                };
            }
        }

        // A "generated" status with no query is malformed model output, not a real query.
        if (string.Equals(proposal.Status, "generated", StringComparison.Ordinal) && query is null)
        {
            return new QueryGenerationResult
            {
                Status = "error",
                Rationale = "The provider reported a generated query but returned no saved-query content.",
                Provider = providerId,
                Model = model
            };
        }

        return new QueryGenerationResult
        {
            Status = proposal.Status,
            Query = string.Equals(proposal.Status, "generated", StringComparison.Ordinal) ? query : null,
            Rationale = proposal.Rationale,
            Clarifications = MapClarifications(proposal.Clarifications),
            Validation = validation,
            UnmappedRequests = proposal.UnmappedRequests,
            CapabilityState = proposal.CapabilityState is null
                ? null
                : new QueryGenerationCapabilityState
                {
                    Name = proposal.CapabilityState.Name,
                    State = proposal.CapabilityState.State,
                    Reason = proposal.CapabilityState.Reason
                },
            Provider = providerId,
            Model = model
        };
    }

    private async Task<QueryGenerationModelProposal> CallModelAsync(
        QueryGenerationProviderRequest request,
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
                    new OpenAiMessage { Role = "system", Content = QueryGenerationPrompt.BuildSystem(request) },
                    new OpenAiMessage { Role = "user", Content = QueryGenerationPrompt.BuildUser(request) }
                ],
                ResponseFormat = new OpenAiResponseFormat
                {
                    Type = "json_schema",
                    JsonSchema = new OpenAiJsonSchema
                    {
                        Name = "query_proposal",
                        Strict = true,
                        Schema = QueryGenerationSchema.Build()
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

            var proposal = JsonSerializer.Deserialize(content, QueryGenerationJsonContext.Default.QueryGenerationModelProposal);
            return proposal ?? ErrorProposal("Failed to deserialize the query proposal from the provider response.");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            return ErrorProposal("Provider request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return ErrorProposal("Provider request failed.");
        }
        catch (JsonException)
        {
            return ErrorProposal("Provider response could not be parsed.");
        }
    }

    private static QueryGenerationResult Unsupported(string reason) => new()
    {
        Status = "unsupported",
        Rationale = reason
    };

    private static QueryGenerationModelProposal ErrorProposal(string reason) => new()
    {
        Status = "error",
        Rationale = reason
    };

    /// <summary>
    /// Forces the server-owned fields onto the generated query (SavedQueryContent is immutable): the
    /// echoed naturalLanguageQuery is always the caller's prompt, never the model's paraphrase. Mirrors
    /// the form gate's NormalizeSchemaVersion (server owns the value; the model is not trusted with it).
    /// </summary>
    private static SavedQueryContent? NormalizeServerOwnedFields(SavedQueryContent? query, string prompt)
    {
        if (query is null)
        {
            return null;
        }

        if (string.Equals(query.NaturalLanguageQuery, prompt, StringComparison.Ordinal))
        {
            return query;
        }

        return new SavedQueryContent
        {
            NaturalLanguageQuery = prompt,
            LayerId = query.LayerId,
            ServiceName = query.ServiceName,
            FilterPlan = query.FilterPlan,
            OutFields = query.OutFields,
            OutputSrid = query.OutputSrid,
            PreviewLimit = query.PreviewLimit,
            OutputFormat = query.OutputFormat,
            Units = query.Units,
            Metadata = query.Metadata
        };
    }

    private static QueryGenerationClarification[] MapClarifications(QueryGenerationModelClarification[] clarifications) =>
        clarifications
            .Select(c => new QueryGenerationClarification
            {
                Id = c.Id,
                Kind = c.Kind,
                Prompt = c.Prompt,
                Reason = c.Reason,
                Choices = c.Choices
                    .Select(choice => new QueryGenerationClarificationChoice { Id = choice.Id, Label = choice.Label, Effect = choice.Effect })
                    .ToArray()
            })
            .ToArray();
}
