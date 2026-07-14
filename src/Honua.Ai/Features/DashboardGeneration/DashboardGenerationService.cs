// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Honua.Ai.Providers.AzureOpenAi;
using Honua.Ai.Providers.Bedrock;
using Honua.Ai.WorkflowGeneration;
using Honua.Ai.WorkflowGeneration.Models;
using Honua.Core.Features.Publishing.Dashboards;
using Honua.Core.Features.WorkflowPackages.Generation;
using Microsoft.Extensions.Options;

namespace Honua.Ai.DashboardGeneration;

/// <summary>
/// Default <see cref="IDashboardGenerationService"/>: grounds a prompt in the dashboard panel/chart
/// (Vega-Lite) vocabulary, calls an OpenAI-compatible provider (local/openai) with a strict json_schema,
/// and applies <see cref="DashboardDocumentValidator"/> as a structural generation gate with a bounded
/// repair loop. Reuses the workflow-generation provider configuration + chat plumbing so a single local
/// model serves all generation families. Like report generation there is no DB-backed publish validator,
/// so the gate is structural-only and the proposed document is returned as an opaque
/// <see cref="JsonElement"/> for the console to round-trip. Mirrors <c>ReportGenerationService</c>.
/// </summary>
public sealed class DashboardGenerationService : IDashboardGenerationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WorkflowGenerationConfiguration _configuration;
    private readonly WorkflowGenerationApiKeyResolver _apiKeyResolver;
    private readonly IBedrockChatClientFactory _bedrockChatClientFactory;
    private readonly AzureOpenAiAuthResolver _azureAuthResolver;
    private readonly ILogger<DashboardGenerationService> _logger;

    public DashboardGenerationService(
        IHttpClientFactory httpClientFactory,
        IOptions<WorkflowGenerationConfiguration> options,
        WorkflowGenerationApiKeyResolver apiKeyResolver,
        IBedrockChatClientFactory bedrockChatClientFactory,
        ILogger<DashboardGenerationService> logger,
        AzureOpenAiAuthResolver? azureAuthResolver = null)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = options.Value;
        _apiKeyResolver = apiKeyResolver;
        _bedrockChatClientFactory = bedrockChatClientFactory;
        _azureAuthResolver = azureAuthResolver
            ?? new AzureOpenAiAuthResolver(apiKeyResolver, new DefaultAzureOpenAiTokenProvider());
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DashboardGenerationResult> GenerateAsync(DashboardGenerationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_configuration.Enabled)
        {
            return Unsupported("AI dashboard generation is disabled on this server.");
        }

        var providerId = string.IsNullOrWhiteSpace(request.Provider) ? _configuration.DefaultProvider : request.Provider!;
        var options = _configuration.GetProvider(providerId);

        // Bedrock targets the regional runtime endpoint via the AWS credential chain, so it needs no
        // endpoint URL — only a model id. OpenAI-compatible/Anthropic providers require an endpoint.
        var isBedrock = string.Equals(providerId, WorkflowGenerationConfiguration.BedrockProviderId, StringComparison.OrdinalIgnoreCase);
        var isAzureOpenAi = string.Equals(providerId, WorkflowGenerationConfiguration.AzureOpenAiProviderId, StringComparison.OrdinalIgnoreCase);
        var endpointMissing = !isBedrock && string.IsNullOrWhiteSpace(options?.Endpoint);
        // Azure OpenAI routes by deployment name, so a configured deployment satisfies the
        // model requirement even when no raw model id is set.
        var modelMissing = string.IsNullOrWhiteSpace(options?.Model)
            && !(isAzureOpenAi && !string.IsNullOrWhiteSpace(options?.Deployment));
        if (options is null || endpointMissing || modelMissing)
        {
            return Unsupported($"Dashboard generation provider '{providerId}' is not configured on this server.");
        }

        var model = string.IsNullOrWhiteSpace(request.Model)
            ? (string.IsNullOrWhiteSpace(options.Model) ? options.Deployment : options.Model)
            : request.Model!;

        var providerRequest = new DashboardGenerationProviderRequest
        {
            Prompt = request.Prompt,
            ModelOverride = request.Model,
            Conversation = request.Conversation,
            Answers = request.Answers,
            CurrentDashboard = ParseCurrentDashboard(request.CurrentDocument)
        };

        var proposal = await CallModelAsync(providerRequest, options, providerId, model, cancellationToken).ConfigureAwait(false);

        // The server owns the format discriminator; never depend on the model for it. Force the canonical
        // value before validating + returning.
        var dashboard = NormalizeFormat(proposal.Dashboard);

        if (string.Equals(proposal.Status, "generated", StringComparison.Ordinal) && dashboard is not null)
        {
            var validation = DashboardDocumentValidator.Validate(dashboard);
            var gate = DashboardGenerationValidationGate.Evaluate(validation);

            var attempts = 0;
            while (!gate.Passed && attempts < _configuration.MaxRepairAttempts)
            {
                attempts++;
                var repair = providerRequest with { RepairFailures = gate.StructuralFailures };
                proposal = await CallModelAsync(repair, options, providerId, model, cancellationToken).ConfigureAwait(false);
                dashboard = NormalizeFormat(proposal.Dashboard);
                if (!string.Equals(proposal.Status, "generated", StringComparison.Ordinal) || dashboard is null)
                {
                    break;
                }

                validation = DashboardDocumentValidator.Validate(dashboard);
                gate = DashboardGenerationValidationGate.Evaluate(validation);
            }

            if (string.Equals(proposal.Status, "generated", StringComparison.Ordinal) && dashboard is not null && !gate.Passed)
            {
                var summary = string.Join("; ", gate.StructuralFailures.Select(f => f.Message));
                return new DashboardGenerationResult
                {
                    Status = "error",
                    Rationale = "The proposed dashboard did not pass structural validation: " + summary,
                    Provider = providerId,
                    Model = model
                };
            }
        }

        var generated = string.Equals(proposal.Status, "generated", StringComparison.Ordinal) && dashboard is not null;
        return new DashboardGenerationResult
        {
            Status = proposal.Status,
            Document = generated ? SerializeDashboard(dashboard!) : null,
            RouteSlug = generated ? (proposal.RouteSlug ?? dashboard!.RouteSlug) : proposal.RouteSlug,
            Rationale = proposal.Rationale,
            Clarifications = MapClarifications(proposal.Clarifications),
            UnmappedRequests = proposal.UnmappedRequests,
            CapabilityState = proposal.CapabilityState is null
                ? null
                : new DashboardGenerationCapabilityState
                {
                    Name = proposal.CapabilityState.Name,
                    State = proposal.CapabilityState.State,
                    Reason = proposal.CapabilityState.Reason
                },
            Provider = providerId,
            Model = model
        };
    }

    private async Task<DashboardGenerationModelProposal> CallModelAsync(
        DashboardGenerationProviderRequest request,
        WorkflowGenerationProviderOptions options,
        string providerId,
        string model,
        CancellationToken cancellationToken)
    {
        // The AWS Bedrock (Claude) provider has no OpenAI-compatible /chat/completions surface;
        // route it through the Converse API (forced tool call for structured output) instead.
        if (string.Equals(providerId, WorkflowGenerationConfiguration.BedrockProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return await CallBedrockAsync(request, options, providerId, model, cancellationToken).ConfigureAwait(false);
        }

        // Azure OpenAI is OpenAI-compatible but deployment-routed (Azure URL + api-key/Entra auth);
        // route it through the shared Azure structured-generation client.
        if (string.Equals(providerId, WorkflowGenerationConfiguration.AzureOpenAiProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return await CallAzureOpenAiAsync(request, options, providerId, model, cancellationToken).ConfigureAwait(false);
        }

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
                    new OpenAiMessage { Role = "system", Content = DashboardGenerationPrompt.BuildSystem(request) },
                    new OpenAiMessage { Role = "user", Content = DashboardGenerationPrompt.BuildUser(request) }
                ],
                ResponseFormat = new OpenAiResponseFormat
                {
                    Type = "json_schema",
                    JsonSchema = new OpenAiJsonSchema
                    {
                        Name = "dashboard_proposal",
                        Strict = true,
                        Schema = DashboardGenerationSchema.Build()
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

            var proposal = JsonSerializer.Deserialize(content, DashboardGenerationJsonContext.Default.DashboardGenerationModelProposal);
            return proposal ?? ErrorProposal("Failed to deserialize the dashboard proposal from the provider response.");
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

    private async Task<DashboardGenerationModelProposal> CallBedrockAsync(
        DashboardGenerationProviderRequest request,
        WorkflowGenerationProviderOptions options,
        string providerId,
        string model,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await BedrockStructuredGenerationClient.GenerateAsync(
                options,
                model,
                DashboardGenerationPrompt.BuildSystem(request),
                DashboardGenerationPrompt.BuildUser(request),
                DashboardGenerationSchema.Build(),
                "Emit the proposed dashboard.document (or a clarification/refusal).",
                _bedrockChatClientFactory.Create,
                cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                GenerationProviderLog.ProviderRequestFailed(_logger, providerId, new InvalidOperationException(result.Error));
                return ErrorProposal(result.Error ?? "Bedrock request failed.");
            }

            var proposal = JsonSerializer.Deserialize(result.Json!, DashboardGenerationJsonContext.Default.DashboardGenerationModelProposal);
            return proposal ?? ErrorProposal("Failed to deserialize the dashboard proposal from the Bedrock response.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            GenerationProviderLog.ProviderResponseParseFailed(_logger, providerId, ex);
            return ErrorProposal("Bedrock response could not be parsed.");
        }
        // Intentionally generic: this is a provider-boundary call to the Bedrock SDK, which
        // surfaces transport/auth/throttling failures beyond the specific types already handled
        // above; map any remaining failure to a generic proposal error instead of crashing the caller.
        catch (Exception ex)
        {
            GenerationProviderLog.ProviderRequestFailed(_logger, providerId, ex);
            return ErrorProposal("Bedrock request failed.");
        }
    }

    private async Task<DashboardGenerationModelProposal> CallAzureOpenAiAsync(
        DashboardGenerationProviderRequest request,
        WorkflowGenerationProviderOptions options,
        string providerId,
        string model,
        CancellationToken cancellationToken)
    {
        try
        {
            var credential = await _azureAuthResolver.ResolveAsync(providerId, options, cancellationToken).ConfigureAwait(false);

            var client = _httpClientFactory.CreateClient("workflow-generation");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

            var result = await AzureOpenAiStructuredGenerationClient.GenerateAsync(
                client,
                options,
                model,
                credential.ApiKey,
                credential.AccessToken,
                DashboardGenerationPrompt.BuildSystem(request),
                DashboardGenerationPrompt.BuildUser(request),
                "dashboard_proposal",
                DashboardGenerationSchema.Build(),
                cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                GenerationProviderLog.ProviderRequestFailed(_logger, providerId, new InvalidOperationException(result.Error));
                return ErrorProposal(result.Error ?? "Azure OpenAI request failed.");
            }

            var proposal = JsonSerializer.Deserialize(result.Json!, DashboardGenerationJsonContext.Default.DashboardGenerationModelProposal);
            return proposal ?? ErrorProposal("Failed to deserialize the dashboard proposal from the Azure OpenAI response.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            GenerationProviderLog.ProviderResponseParseFailed(_logger, providerId, ex);
            return ErrorProposal("Azure OpenAI response could not be parsed.");
        }
        catch (HttpRequestException ex)
        {
            GenerationProviderLog.ProviderRequestFailed(_logger, providerId, ex);
            return ErrorProposal("Azure OpenAI request failed.");
        }
    }

    private static DashboardGenerationResult Unsupported(string reason) => new()
    {
        Status = "unsupported",
        Rationale = reason
    };

    private static DashboardGenerationModelProposal ErrorProposal(string reason) => new()
    {
        Status = "error",
        Rationale = reason
    };

    /// <summary>Returns the dashboard with the server-canonical format discriminator (DashboardDocument is immutable).</summary>
    private static DashboardDocument? NormalizeFormat(DashboardDocument? dashboard)
    {
        if (dashboard is null || string.Equals(dashboard.Format, DashboardDocumentFormats.V1, StringComparison.Ordinal))
        {
            return dashboard;
        }

        return new DashboardDocument
        {
            Format = DashboardDocumentFormats.V1,
            Title = dashboard.Title,
            Description = dashboard.Description,
            Narrative = dashboard.Narrative,
            RouteSlug = dashboard.RouteSlug,
            Breakpoint = dashboard.Breakpoint,
            Bindings = dashboard.Bindings,
            Panels = dashboard.Panels
        };
    }

    /// <summary>Parses the console's raw dashboard-document payload into a typed dashboard for refine grounding; null on any failure.</summary>
    private static DashboardDocument? ParseCurrentDashboard(JsonElement? document)
    {
        if (document is not { } element || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return element.Deserialize(DashboardDocumentJsonContext.Default.DashboardDocument);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Serializes the typed dashboard into the opaque honua.dashboard-document.v1 JSON element the console round-trips.</summary>
    private static JsonElement SerializeDashboard(DashboardDocument dashboard)
    {
        var json = JsonSerializer.Serialize(dashboard, DashboardDocumentJsonContext.Default.DashboardDocument);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static DashboardGenerationClarification[] MapClarifications(DashboardGenerationModelClarification[] clarifications) =>
        clarifications
            .Select(c => new DashboardGenerationClarification
            {
                Id = c.Id,
                Kind = c.Kind,
                Prompt = c.Prompt,
                Reason = c.Reason,
                Choices = c.Choices
                    .Select(choice => new DashboardGenerationClarificationChoice { Id = choice.Id, Label = choice.Label, Effect = choice.Effect })
                    .ToArray()
            })
            .ToArray();
}
