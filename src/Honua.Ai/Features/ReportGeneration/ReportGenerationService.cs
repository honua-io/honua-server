// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Honua.Ai.WorkflowGeneration.Models;
using Honua.Core.Features.Publishing.Reports;
using Honua.Core.Features.WorkflowPackages.Generation;
using Microsoft.Extensions.Options;

namespace Honua.Ai.ReportGeneration;

/// <summary>
/// Default <see cref="IReportGenerationService"/>: grounds a prompt in the report panel/chart (Vega-Lite)
/// vocabulary, calls an OpenAI-compatible provider (local/openai) with a strict json_schema, and applies
/// <see cref="ReportDocumentValidator"/> as a structural generation gate with a bounded repair loop. Reuses
/// the workflow-generation provider configuration + chat plumbing so a single local model serves all three
/// families. Unlike form generation there is no DB-backed publish validator, so the gate is structural-only
/// and the proposed document is returned as an opaque <see cref="JsonElement"/> for the console to round-trip.
/// </summary>
public sealed class ReportGenerationService : IReportGenerationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WorkflowGenerationConfiguration _configuration;

    public ReportGenerationService(
        IHttpClientFactory httpClientFactory,
        IOptions<WorkflowGenerationConfiguration> options)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = options.Value;
    }

    /// <inheritdoc />
    public async Task<ReportGenerationResult> GenerateAsync(ReportGenerationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_configuration.Enabled)
        {
            return Unsupported("AI report generation is disabled on this server.");
        }

        var providerId = string.IsNullOrWhiteSpace(request.Provider) ? _configuration.DefaultProvider : request.Provider!;
        var options = _configuration.GetProvider(providerId);
        if (options is null || string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.Model))
        {
            return Unsupported($"Report generation provider '{providerId}' is not configured on this server.");
        }

        var model = string.IsNullOrWhiteSpace(request.Model) ? options.Model! : request.Model!;

        var providerRequest = new ReportGenerationProviderRequest
        {
            Prompt = request.Prompt,
            ModelOverride = request.Model,
            Conversation = request.Conversation,
            Answers = request.Answers,
            CurrentReport = ParseCurrentReport(request.CurrentDocument)
        };

        var proposal = await CallModelAsync(providerRequest, options, providerId, model, cancellationToken).ConfigureAwait(false);

        // The server owns the format discriminator; never depend on the model for it. Force the canonical
        // value before validating + returning.
        var report = NormalizeFormat(proposal.Report);

        if (string.Equals(proposal.Status, "generated", StringComparison.Ordinal) && report is not null)
        {
            var validation = ReportDocumentValidator.Validate(report);
            var gate = ReportGenerationValidationGate.Evaluate(validation);

            var attempts = 0;
            while (!gate.Passed && attempts < _configuration.MaxRepairAttempts)
            {
                attempts++;
                var repair = providerRequest with { RepairFailures = gate.StructuralFailures };
                proposal = await CallModelAsync(repair, options, providerId, model, cancellationToken).ConfigureAwait(false);
                report = NormalizeFormat(proposal.Report);
                if (!string.Equals(proposal.Status, "generated", StringComparison.Ordinal) || report is null)
                {
                    break;
                }

                validation = ReportDocumentValidator.Validate(report);
                gate = ReportGenerationValidationGate.Evaluate(validation);
            }

            if (string.Equals(proposal.Status, "generated", StringComparison.Ordinal) && report is not null && !gate.Passed)
            {
                var summary = string.Join("; ", gate.StructuralFailures.Select(f => f.Message));
                return new ReportGenerationResult
                {
                    Status = "error",
                    Rationale = "The proposed report did not pass structural validation: " + summary,
                    Provider = providerId,
                    Model = model
                };
            }
        }

        var generated = string.Equals(proposal.Status, "generated", StringComparison.Ordinal) && report is not null;
        return new ReportGenerationResult
        {
            Status = proposal.Status,
            Document = generated ? SerializeReport(report!) : null,
            RouteSlug = generated ? (proposal.RouteSlug ?? report!.RouteSlug) : proposal.RouteSlug,
            Rationale = proposal.Rationale,
            Clarifications = MapClarifications(proposal.Clarifications),
            UnmappedRequests = proposal.UnmappedRequests,
            CapabilityState = proposal.CapabilityState is null
                ? null
                : new ReportGenerationCapabilityState
                {
                    Name = proposal.CapabilityState.Name,
                    State = proposal.CapabilityState.State,
                    Reason = proposal.CapabilityState.Reason
                },
            Provider = providerId,
            Model = model
        };
    }

    private async Task<ReportGenerationModelProposal> CallModelAsync(
        ReportGenerationProviderRequest request,
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
                    new OpenAiMessage { Role = "system", Content = ReportGenerationPrompt.BuildSystem(request) },
                    new OpenAiMessage { Role = "user", Content = ReportGenerationPrompt.BuildUser(request) }
                ],
                ResponseFormat = new OpenAiResponseFormat
                {
                    Type = "json_schema",
                    JsonSchema = new OpenAiJsonSchema
                    {
                        Name = "report_proposal",
                        Strict = true,
                        Schema = ReportGenerationSchema.Build()
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

            var proposal = JsonSerializer.Deserialize(content, ReportGenerationJsonContext.Default.ReportGenerationModelProposal);
            return proposal ?? ErrorProposal("Failed to deserialize the report proposal from the provider response.");
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

    private static ReportGenerationResult Unsupported(string reason) => new()
    {
        Status = "unsupported",
        Rationale = reason
    };

    private static ReportGenerationModelProposal ErrorProposal(string reason) => new()
    {
        Status = "error",
        Rationale = reason
    };

    /// <summary>Returns the report with the server-canonical format discriminator (ReportDocument is immutable).</summary>
    private static ReportDocument? NormalizeFormat(ReportDocument? report)
    {
        if (report is null || string.Equals(report.Format, ReportDocumentFormats.V1, StringComparison.Ordinal))
        {
            return report;
        }

        return new ReportDocument
        {
            Format = ReportDocumentFormats.V1,
            Title = report.Title,
            Description = report.Description,
            Narrative = report.Narrative,
            RouteSlug = report.RouteSlug,
            Bindings = report.Bindings,
            Panels = report.Panels
        };
    }

    /// <summary>Parses the console's raw report-document payload into a typed report for refine grounding; null on any failure.</summary>
    private static ReportDocument? ParseCurrentReport(JsonElement? document)
    {
        if (document is not { } element || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return element.Deserialize(ReportDocumentJsonContext.Default.ReportDocument);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Serializes the typed report into the opaque honua.report-document.v1 JSON element the console round-trips.</summary>
    private static JsonElement SerializeReport(ReportDocument report)
    {
        var json = JsonSerializer.Serialize(report, ReportDocumentJsonContext.Default.ReportDocument);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static ReportGenerationClarification[] MapClarifications(ReportGenerationModelClarification[] clarifications) =>
        clarifications
            .Select(c => new ReportGenerationClarification
            {
                Id = c.Id,
                Kind = c.Kind,
                Prompt = c.Prompt,
                Reason = c.Reason,
                Choices = c.Choices
                    .Select(choice => new ReportGenerationClarificationChoice { Id = choice.Id, Label = choice.Label, Effect = choice.Effect })
                    .ToArray()
            })
            .ToArray();
}
