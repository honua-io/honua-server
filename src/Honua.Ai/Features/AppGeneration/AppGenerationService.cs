// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Honua.Ai.WorkflowGeneration.Models;
using Honua.Core.Features.WorkflowPackages.Generation;
using Microsoft.Extensions.Options;

namespace Honua.Ai.AppGeneration;

/// <summary>
/// Default <see cref="IAppGenerationService"/>: grounds a prompt in the studio-app/v1 vocabulary
/// (component kinds, permission tiers, visibility tiers), calls an OpenAI-compatible provider
/// (local/openai) with a strict json_schema, and applies <see cref="AppGenerationStructuralValidator"/>
/// as a generation-lenient gate (structural failures only; content binding deferred to publish) with a
/// bounded repair loop. Reuses the workflow-generation provider configuration + chat plumbing so a single
/// local model serves the workflow, form, map, and app families. Like the dashboard service there is no
/// DB-backed publish validator, so the proposed app is returned as an opaque <see cref="JsonElement"/>
/// (the console's studio-app/v1 body) for the console to round-trip directly. Mirrors
/// <c>MapGenerationService</c>.
/// </summary>
public sealed class AppGenerationService : IAppGenerationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WorkflowGenerationConfiguration _configuration;

    public AppGenerationService(
        IHttpClientFactory httpClientFactory,
        IOptions<WorkflowGenerationConfiguration> options)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = options.Value;
    }

    /// <inheritdoc />
    public async Task<AppGenerationResult> GenerateAsync(AppGenerationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_configuration.Enabled)
        {
            return Unsupported("AI app generation is disabled on this server.");
        }

        var providerId = string.IsNullOrWhiteSpace(request.Provider) ? _configuration.DefaultProvider : request.Provider!;
        var options = _configuration.GetProvider(providerId);
        if (options is null || string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.Model))
        {
            return Unsupported($"App generation provider '{providerId}' is not configured on this server.");
        }

        var model = string.IsNullOrWhiteSpace(request.Model) ? options.Model! : request.Model!;

        var providerRequest = new AppGenerationProviderRequest
        {
            Prompt = request.Prompt,
            ModelOverride = request.Model,
            Conversation = request.Conversation,
            Answers = request.Answers,
            CurrentApp = request.CurrentApp
        };

        var proposal = await CallModelAsync(providerRequest, options, providerId, model, cancellationToken).ConfigureAwait(false);

        // The server owns the schemaVersion discriminator; never depend on the model for it. Force the
        // canonical value before validating + returning.
        var app = NormalizeSchemaVersion(proposal.App);

        AppPackageValidationResult? validation = null;
        if (string.Equals(proposal.Status, "generated", StringComparison.Ordinal) && app is not null)
        {
            validation = AppGenerationStructuralValidator.Validate(app);
            var gate = AppGenerationValidationGate.Evaluate(validation);

            var attempts = 0;
            while (!gate.Passed && attempts < _configuration.MaxRepairAttempts)
            {
                attempts++;
                var repair = providerRequest with { RepairFailures = gate.StructuralFailures };
                proposal = await CallModelAsync(repair, options, providerId, model, cancellationToken).ConfigureAwait(false);
                app = NormalizeSchemaVersion(proposal.App);
                if (!string.Equals(proposal.Status, "generated", StringComparison.Ordinal) || app is null)
                {
                    break;
                }

                validation = AppGenerationStructuralValidator.Validate(app);
                gate = AppGenerationValidationGate.Evaluate(validation);
            }

            if (string.Equals(proposal.Status, "generated", StringComparison.Ordinal) && app is not null && !gate.Passed)
            {
                var summary = string.Join("; ", gate.StructuralFailures.Select(f => f.Message));
                return new AppGenerationResult
                {
                    Status = "error",
                    Rationale = "The proposed app did not pass server validation: " + summary,
                    Validation = validation,
                    Provider = providerId,
                    Model = model
                };
            }
        }

        return new AppGenerationResult
        {
            Status = proposal.Status,
            Package = string.Equals(proposal.Status, "generated", StringComparison.Ordinal) ? app : null,
            Rationale = proposal.Rationale,
            Clarifications = MapClarifications(proposal.Clarifications),
            Validation = validation,
            UnmappedRequests = proposal.UnmappedRequests,
            CapabilityState = proposal.CapabilityState is null
                ? null
                : new AppGenerationCapabilityState
                {
                    Name = proposal.CapabilityState.Name,
                    State = proposal.CapabilityState.State,
                    Reason = proposal.CapabilityState.Reason
                },
            Provider = providerId,
            Model = model
        };
    }

    private async Task<AppGenerationModelProposal> CallModelAsync(
        AppGenerationProviderRequest request,
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
                    new OpenAiMessage { Role = "system", Content = AppGenerationPrompt.BuildSystem(request) },
                    new OpenAiMessage { Role = "user", Content = AppGenerationPrompt.BuildUser(request) }
                ],
                ResponseFormat = new OpenAiResponseFormat
                {
                    Type = "json_schema",
                    JsonSchema = new OpenAiJsonSchema
                    {
                        Name = "app_proposal",
                        Strict = true,
                        Schema = AppGenerationSchema.Build()
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

            var proposal = JsonSerializer.Deserialize(content, AppGenerationJsonContext.Default.AppGenerationModelProposal);
            return proposal ?? ErrorProposal("Failed to deserialize the app proposal from the provider response.");
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

    private static AppGenerationResult Unsupported(string reason) => new()
    {
        Status = "unsupported",
        Rationale = reason
    };

    private static AppGenerationModelProposal ErrorProposal(string reason) => new()
    {
        Status = "error",
        Rationale = reason
    };

    /// <summary>
    /// Returns the app body with the server-canonical schemaVersion forced to studio-app/v1. The model
    /// may emit a wrong (or missing) schemaVersion despite the schema; the console requires the canonical
    /// discriminator to round-trip the envelope. Rewrites the opaque object element with the corrected
    /// (or inserted) schemaVersion, preserving every other property.
    /// </summary>
    private static JsonElement? NormalizeSchemaVersion(JsonElement? app)
    {
        if (app is not { } body || body.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (body.TryGetProperty("schemaVersion", out var version)
            && version.ValueKind == JsonValueKind.String
            && string.Equals(version.GetString(), AppGenerationStructuralValidator.AppPackageSchemaVersion, StringComparison.Ordinal))
        {
            return body;
        }

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", AppGenerationStructuralValidator.AppPackageSchemaVersion);
            foreach (var property in body.EnumerateObject())
            {
                if (string.Equals(property.Name, "schemaVersion", StringComparison.Ordinal))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static AppGenerationClarification[] MapClarifications(AppGenerationModelClarification[] clarifications) =>
        clarifications
            .Select(c => new AppGenerationClarification
            {
                Id = c.Id,
                Kind = c.Kind,
                Prompt = c.Prompt,
                Reason = c.Reason,
                Choices = c.Choices
                    .Select(choice => new AppGenerationClarificationChoice { Id = choice.Id, Label = choice.Label, Effect = choice.Effect })
                    .ToArray()
            })
            .ToArray();
}
