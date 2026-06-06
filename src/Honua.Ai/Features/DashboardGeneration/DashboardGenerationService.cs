// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

    public DashboardGenerationService(
        IHttpClientFactory httpClientFactory,
        IOptions<WorkflowGenerationConfiguration> options)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = options.Value;
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
        if (options is null || string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.Model))
        {
            return Unsupported($"Dashboard generation provider '{providerId}' is not configured on this server.");
        }

        var model = string.IsNullOrWhiteSpace(request.Model) ? options.Model! : request.Model!;

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

            var proposal = JsonSerializer.Deserialize(content, DashboardGenerationJsonContext.Default.DashboardGenerationModelProposal);
            return proposal ?? ErrorProposal("Failed to deserialize the dashboard proposal from the provider response.");
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
        return JsonDocument.Parse(json).RootElement.Clone();
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
