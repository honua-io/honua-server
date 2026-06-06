// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Honua.Ai.WorkflowGeneration.Models;
using Honua.Core.Configuration;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.WorkflowPackages.Generation;
using Microsoft.Extensions.Options;

namespace Honua.Ai.FormGeneration;

/// <summary>
/// Default <see cref="IFormGenerationService"/>: grounds a prompt in the form field-type vocabulary,
/// calls an OpenAI-compatible provider (local/openai) with a strict json_schema, and applies
/// <see cref="FormPackageValidator"/> as a generation-lenient gate (structural failures only; target
/// binding deferred to publish) with a bounded repair loop. Reuses the workflow-generation provider
/// configuration + chat plumbing so a single local model serves both families.
/// </summary>
public sealed class FormGenerationService : IFormGenerationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WorkflowGenerationConfiguration _configuration;
    private readonly FormPackageValidator _validator;

    public FormGenerationService(
        IHttpClientFactory httpClientFactory,
        IOptions<WorkflowGenerationConfiguration> options,
        IOptions<LimitsOptions> limitsOptions)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = options.Value;

        // Generation-only validator: a stub target resolver runs the DB-free structural checks and
        // defers target binding to publish (so generation does not require a live metadata database).
        _validator = new FormPackageValidator(new GenerationFormTargetResolver(), limitsOptions);
    }

    /// <inheritdoc />
    public async Task<FormGenerationResult> GenerateAsync(FormGenerationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_configuration.Enabled)
        {
            return Unsupported("AI form generation is disabled on this server.");
        }

        var providerId = string.IsNullOrWhiteSpace(request.Provider) ? _configuration.DefaultProvider : request.Provider!;
        var options = _configuration.GetProvider(providerId);
        if (options is null || string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.Model))
        {
            return Unsupported($"Form generation provider '{providerId}' is not configured on this server.");
        }

        var model = string.IsNullOrWhiteSpace(request.Model) ? options.Model! : request.Model!;

        var providerRequest = new FormGenerationProviderRequest
        {
            Prompt = request.Prompt,
            ModelOverride = request.Model,
            Conversation = request.Conversation,
            Answers = request.Answers,
            CurrentForm = request.CurrentForm
        };

        var proposal = await CallModelAsync(providerRequest, options, providerId, model, cancellationToken).ConfigureAwait(false);

        // The server owns schemaVersion; never depend on the model for it (and a local model may emit
        // a wrong value despite the schema). Force the canonical value before validating + returning.
        var form = NormalizeSchemaVersion(proposal.Form);

        FormPackageValidationResult? validation = null;
        if (string.Equals(proposal.Status, "generated", StringComparison.Ordinal) && form is not null)
        {
            validation = await _validator.ValidateForPublishAsync(form, cancellationToken).ConfigureAwait(false);
            var gate = FormGenerationValidationGate.Evaluate(validation);

            var attempts = 0;
            while (!gate.Passed && attempts < _configuration.MaxRepairAttempts)
            {
                attempts++;
                var repair = providerRequest with { RepairFailures = gate.StructuralFailures };
                proposal = await CallModelAsync(repair, options, providerId, model, cancellationToken).ConfigureAwait(false);
                form = NormalizeSchemaVersion(proposal.Form);
                if (!string.Equals(proposal.Status, "generated", StringComparison.Ordinal) || form is null)
                {
                    break;
                }

                validation = await _validator.ValidateForPublishAsync(form, cancellationToken).ConfigureAwait(false);
                gate = FormGenerationValidationGate.Evaluate(validation);
            }

            if (string.Equals(proposal.Status, "generated", StringComparison.Ordinal) && form is not null && !gate.Passed)
            {
                var summary = string.Join("; ", gate.StructuralFailures.Select(f => f.Message));
                return new FormGenerationResult
                {
                    Status = "error",
                    Rationale = "The proposed form did not pass server validation: " + summary,
                    Validation = validation,
                    Provider = providerId,
                    Model = model
                };
            }
        }

        return new FormGenerationResult
        {
            Status = proposal.Status,
            Package = string.Equals(proposal.Status, "generated", StringComparison.Ordinal) ? form : null,
            Rationale = proposal.Rationale,
            Clarifications = MapClarifications(proposal.Clarifications),
            Validation = validation,
            UnmappedRequests = proposal.UnmappedRequests,
            CapabilityState = proposal.CapabilityState is null
                ? null
                : new FormGenerationCapabilityState
                {
                    Name = proposal.CapabilityState.Name,
                    State = proposal.CapabilityState.State,
                    Reason = proposal.CapabilityState.Reason
                },
            Provider = providerId,
            Model = model
        };
    }

    private async Task<FormGenerationModelProposal> CallModelAsync(
        FormGenerationProviderRequest request,
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
                    new OpenAiMessage { Role = "system", Content = FormGenerationPrompt.BuildSystem(request) },
                    new OpenAiMessage { Role = "user", Content = FormGenerationPrompt.BuildUser(request) }
                ],
                ResponseFormat = new OpenAiResponseFormat
                {
                    Type = "json_schema",
                    JsonSchema = new OpenAiJsonSchema
                    {
                        Name = "form_proposal",
                        Strict = true,
                        Schema = FormGenerationSchema.Build()
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

            var proposal = JsonSerializer.Deserialize(content, FormGenerationJsonContext.Default.FormGenerationModelProposal);
            return proposal ?? ErrorProposal("Failed to deserialize the form proposal from the provider response.");
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

    private static FormGenerationResult Unsupported(string reason) => new()
    {
        Status = "unsupported",
        Rationale = reason
    };

    private static FormGenerationModelProposal ErrorProposal(string reason) => new()
    {
        Status = "error",
        Rationale = reason
    };

    private const string FormSchemaVersion = "honua.form-package.v1";

    /// <summary>Returns the form with the server-canonical schemaVersion (FormPackageDocument is immutable).</summary>
    private static FormPackageDocument? NormalizeSchemaVersion(FormPackageDocument? form)
    {
        if (form is null || string.Equals(form.SchemaVersion, FormSchemaVersion, StringComparison.Ordinal))
        {
            return form;
        }

        return new FormPackageDocument
        {
            SchemaVersion = FormSchemaVersion,
            FormId = form.FormId,
            Title = form.Title,
            Description = form.Description,
            Target = form.Target,
            Sections = form.Sections,
            Fields = form.Fields,
            SubmitPolicy = form.SubmitPolicy,
            AttachmentPolicy = form.AttachmentPolicy,
            PrivacyPolicy = form.PrivacyPolicy,
            OfflinePolicy = form.OfflinePolicy,
            Provenance = form.Provenance,
            Metadata = form.Metadata
        };
    }

    private static FormGenerationClarification[] MapClarifications(FormGenerationModelClarification[] clarifications) =>
        clarifications
            .Select(c => new FormGenerationClarification
            {
                Id = c.Id,
                Kind = c.Kind,
                Prompt = c.Prompt,
                Reason = c.Reason,
                Choices = c.Choices
                    .Select(choice => new FormGenerationClarificationChoice { Id = choice.Id, Label = choice.Label, Effect = choice.Effect })
                    .ToArray()
            })
            .ToArray();
}
