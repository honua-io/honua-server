// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Honua.Ai.WorkflowGeneration.Models;
using Honua.Core.Features.WorkflowPackages.Generation;

namespace Honua.Ai.Providers.AzureOpenAi;

/// <summary>
/// Outcome of a single structured Azure OpenAI generation turn. Either <see cref="Json"/> carries
/// the model's structured proposal (the json_schema-constrained completion content), or
/// <see cref="Error"/> describes why no proposal was produced (truncation, empty response, HTTP
/// failure, …). Mirrors <c>BedrockStructuredResult</c> so the calling services treat both cloud
/// backends identically.
/// </summary>
internal readonly record struct AzureOpenAiStructuredResult(string? Json, string? Error)
{
    internal bool Succeeded => Json is not null;

    internal static AzureOpenAiStructuredResult Ok(string json) => new(json, null);

    internal static AzureOpenAiStructuredResult Failed(string error) => new(null, error);
}

/// <summary>
/// Shared Azure OpenAI / Azure AI Foundry backend for the studio generation flows that speak to an
/// OpenAI-compatible <c>/chat/completions</c> endpoint with a strict <c>json_schema</c> response
/// format (workflow, dashboard, report, and the other Vega-Lite document generators).
///
/// Azure OpenAI is OpenAI-API-compatible at the body level, so the request/response DTOs and the
/// strict-schema structured-output handling are reused verbatim from the OpenAI-compatible path —
/// only the deployment-routed URL and the Azure auth header (<c>api-key</c> for a key, or a Bearer
/// Entra/Managed-Identity token) differ. Truncation (<c>finish_reason == "length"</c>) is surfaced
/// as an actionable error rather than an opaque deserialize failure, matching the OpenAI provider.
/// </summary>
internal static class AzureOpenAiStructuredGenerationClient
{
    /// <summary>
    /// Runs one structured generation turn against Azure OpenAI and returns the proposal JSON.
    /// </summary>
    /// <param name="httpClient">A configured client (timeout set by the caller).</param>
    /// <param name="options">Resolved provider options (endpoint, deployment, api-version, limits).</param>
    /// <param name="model">Effective model id (request override or provider default).</param>
    /// <param name="apiKey">Resolved api-key, or empty when using Entra auth.</param>
    /// <param name="accessToken">Resolved Entra/Managed-Identity bearer token, or null.</param>
    /// <param name="systemPrompt">System grounding prompt.</param>
    /// <param name="userPrompt">User prompt.</param>
    /// <param name="schemaName">The json_schema name (for example <c>workflow_proposal</c>).</param>
    /// <param name="schema">The proposal JSON schema the completion must conform to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<AzureOpenAiStructuredResult> GenerateAsync(
        HttpClient httpClient,
        WorkflowGenerationProviderOptions options,
        string model,
        string apiKey,
        string? accessToken,
        string systemPrompt,
        string userPrompt,
        string schemaName,
        JsonElement schema,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        if (string.IsNullOrWhiteSpace(apiKey) && string.IsNullOrWhiteSpace(accessToken))
        {
            return AzureOpenAiStructuredResult.Failed(
                "Azure OpenAI is not authenticated: configure an ApiKey or grant the host a Managed Identity with access to the resource.");
        }

        var chatRequest = new OpenAiChatCompletionRequest
        {
            // Azure routes by deployment in the URL; the body "model" field is ignored but still
            // sent as the effective model id for parity/telemetry.
            Model = model,
            MaxTokens = options.MaxTokens,
            Temperature = 0.0,
            Messages =
            [
                new OpenAiMessage { Role = "system", Content = systemPrompt },
                new OpenAiMessage { Role = "user", Content = userPrompt }
            ],
            ResponseFormat = new OpenAiResponseFormat
            {
                Type = "json_schema",
                JsonSchema = new OpenAiJsonSchema
                {
                    Name = schemaName,
                    Strict = true,
                    Schema = schema
                }
            }
        };

        var deployment = AzureOpenAiEndpoint.ResolveDeployment(options, model);
        var apiVersion = AzureOpenAiEndpoint.ResolveApiVersion(options);
        var uri = AzureOpenAiEndpoint.BuildChatCompletionsUri(options.Endpoint, deployment, apiVersion);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);

        // Azure auth: prefer an Entra/Managed-Identity bearer token (the recommended path); fall
        // back to the resource api-key header when no token is available.
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        else
        {
            httpRequest.Headers.TryAddWithoutValidation("api-key", apiKey);
        }

        var requestJson = JsonSerializer.Serialize(
            chatRequest, WorkflowGenerationJsonContext.Default.OpenAiChatCompletionRequest);
        httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return AzureOpenAiStructuredResult.Failed(
                $"Azure OpenAI returned HTTP {(int)response.StatusCode}. {Truncate(body, 500)}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var chatResponse = JsonSerializer.Deserialize(
            responseJson, WorkflowGenerationJsonContext.Default.OpenAiChatCompletionResponse);

        var choice = chatResponse?.Choices is { Length: > 0 } choices ? choices[0] : null;

        // Surface max_tokens truncation explicitly: a "length" finish_reason means the JSON body is
        // truncated, so deserialization downstream would fail with an opaque message instead of an
        // actionable one (matches the OpenAI-compatible provider, #1979).
        if (string.Equals(choice?.FinishReason, "length", StringComparison.Ordinal))
        {
            return AzureOpenAiStructuredResult.Failed(
                "Azure OpenAI response was truncated (finish_reason=length / max_tokens reached); try a higher MaxTokens.");
        }

        var content = choice?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return AzureOpenAiStructuredResult.Failed("Azure OpenAI returned an empty response.");
        }

        return AzureOpenAiStructuredResult.Ok(content);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
