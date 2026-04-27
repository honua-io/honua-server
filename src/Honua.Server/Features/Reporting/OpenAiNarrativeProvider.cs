// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Reporting;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;
using Honua.Server.Features.Reporting.Models;
using Honua.Server.Features.Reporting.Prompts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Reporting;

/// <summary>
/// LLM narrative provider that calls an OpenAI-compatible chat-completions
/// endpoint. Hash-keys responses by the deterministic baseline payload so
/// repeated renders of an unchanged draft do not burn tokens. Failures are
/// surfaced as exceptions; the builder catches them and falls back to the
/// deterministic baseline.
/// </summary>
internal sealed class OpenAiNarrativeProvider : INarrativeProvider
{
    private const string HttpClientName = "reporting-narrative";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ReportingNarrativeConfiguration _configuration;
    private readonly ILogger<OpenAiNarrativeProvider> _logger;

    public OpenAiNarrativeProvider(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptions<ReportingConfiguration> options,
        ILogger<OpenAiNarrativeProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _configuration = options.Value.Narrative;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsDeterministic => false;

    /// <inheritdoc />
    public async Task<NarrativeFill> GenerateAsync(AnalysisReportDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (!_configuration.Enabled || draft.NarrativeSlots.Count == 0)
        {
            return NarrativeFill.Empty;
        }

        var promptPayload = BuildPromptPayload(draft);
        var promptJson = JsonSerializer.Serialize(promptPayload, ReportingJsonContext.Default.NarrativePromptPayload);
        var cacheKey = BuildCacheKey(draft, promptJson);

        if (_cache.TryGetValue<NarrativeFill>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var fill = await CallProviderAsync(promptJson, cancellationToken).ConfigureAwait(false);
        if (fill.SlotText.Count > 0)
        {
            var ttl = TimeSpan.FromHours(Math.Max(1, _configuration.CacheTtlHours));
            _cache.Set(cacheKey, fill, ttl);
        }
        return fill;
    }

    private async Task<NarrativeFill> CallProviderAsync(string promptJson, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, _configuration.TimeoutSeconds));

        var endpoint = _configuration.Endpoint.TrimEnd('/') + "/chat/completions";
        var chatRequest = new ReportingOpenAiChatRequest
        {
            Model = _configuration.Model,
            MaxTokens = _configuration.MaxTokens,
            Temperature = 0.0,
            ResponseFormat = new ReportingOpenAiResponseFormat { Type = "json_object" },
            Messages =
            [
                new ReportingOpenAiMessage { Role = "system", Content = NarrativeSystemPrompt.Default },
                new ReportingOpenAiMessage { Role = "user", Content = promptJson }
            ]
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _configuration.ApiKey);
        var requestJson = JsonSerializer.Serialize(chatRequest, ReportingJsonContext.Default.ReportingOpenAiChatRequest);
        httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Reporting narrative provider returned HTTP {(int)response.StatusCode}: {body}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var chatResponse = JsonSerializer.Deserialize(responseJson, ReportingJsonContext.Default.ReportingOpenAiChatResponse);
        var content = chatResponse?.Choices?.Length > 0 ? chatResponse.Choices[0].Message?.Content : null;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Reporting narrative provider returned an empty response.");
        }

        var parsed = JsonSerializer.Deserialize(content, ReportingJsonContext.Default.NarrativeFillResponse)
            ?? throw new InvalidOperationException("Reporting narrative provider response could not be deserialized.");

        return new NarrativeFill { SlotText = parsed.Slots };
    }

    private static NarrativePromptPayload BuildPromptPayload(AnalysisReportDraft draft)
    {
        var payload = new NarrativePromptPayload
        {
            TemplateId = draft.TemplateId,
            ProcessId = draft.ProcessId,
            ProcessFamily = draft.ProcessFamily,
            Summary = draft.SourcePackage.Summary.Title,
            Slots = draft.NarrativeSlots
                .Select(slot => new NarrativePromptSlot
                {
                    SlotId = slot.SlotId,
                    Hint = slot.LlmHint,
                    DeterministicText = slot.DeterministicText
                })
                .ToList()
        };

        if (!string.IsNullOrWhiteSpace(draft.SourcePackage.Summary.Description))
        {
            payload.Summary = $"{draft.SourcePackage.Summary.Title}: {draft.SourcePackage.Summary.Description}";
        }

        return payload;
    }

    private static string BuildCacheKey(AnalysisReportDraft draft, string payloadJson)
    {
        var fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson));
        var hex = Convert.ToHexString(fingerprint).ToLowerInvariant();
        return string.Concat("reporting-narrative:", draft.TemplateId, ":", draft.TemplateVersion, ":", hex);
    }
}
