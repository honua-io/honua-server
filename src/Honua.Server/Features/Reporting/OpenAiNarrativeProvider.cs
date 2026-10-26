// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;
using Honua.Core.Features.Reporting;
using Honua.Core.Features.Security.Admission;
using Honua.Core.Features.Security;
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
    private readonly IAiExecutionAdmission? _admission;
    private static readonly byte[] CacheCredentialSalt = RandomNumberGenerator.GetBytes(32);

    public OpenAiNarrativeProvider(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptions<ReportingConfiguration> options,
        ILogger<OpenAiNarrativeProvider> logger,
        IAiExecutionAdmission? admission = null)
    {
        _admission = admission;
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

        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = CaptureConfiguration();
        if (!snapshot.Enabled || draft.NarrativeSlots.Count == 0)
        {
            return NarrativeFill.Empty;
        }

        // Evaluate the current options snapshot before payload creation or cache access.
        if (!AiDisclosurePolicy.IsAllowed(snapshot.Disclosure,
            new AiDisclosureRequest(snapshot.Provider, "openai", snapshot.Endpoint, snapshot.Model, "reporting-narrative")))
        {
            return NarrativeFill.Empty;
        }

        var promptPayload = BuildPromptPayload(draft);
        var promptJson = JsonSerializer.Serialize(promptPayload, ReportingJsonContext.Default.NarrativePromptPayload);
        if (_admission is null || draft.ExecutionContext is not { IsValid: true, UseCase: "reporting-narrative" } executionContext)
            return NarrativeFill.Empty;
        AiFinancialPriceBasis? basis;
        try
        {
            basis = await _admission.GetFinancialBasisAsync(executionContext, cancellationToken).ConfigureAwait(false);
        }
        catch (AiAdmissionException) { return NarrativeFill.Empty; }
        var invocation = FreezeInvocation(promptJson, snapshot, basis);
        var credentialTag = Convert.ToHexStringLower(HMACSHA256.HashData(CacheCredentialSalt, Encoding.UTF8.GetBytes(snapshot.ApiKey)));
        var cacheKey = BuildCacheKey(draft, string.Join('\n', invocation.RequestDigest, invocation.Destination, invocation.Provider, credentialTag));

        if (_cache.TryGetValue<NarrativeFill>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        // Freeze the actual provider wire bytes and effective route before admission
        // and before client/credential preparation. Configuration changes cannot rebind it.
        IAiExecutionPermit permit;
        try
        {
            permit = await _admission.AcquireAsync(executionContext, invocation, cancellationToken).ConfigureAwait(false);
        }
        catch (AiAdmissionException)
        {
            return NarrativeFill.Empty;
        }
        await using var ownedPermit = permit;
        NarrativeFill fill;
        try
        {
            await permit.BeginInvocationAsync(cancellationToken).ConfigureAwait(false);
            fill = await CallProviderAsync(invocation, snapshot, permit, permit.Token).ConfigureAwait(false);
        }
        catch (AiAdmissionException)
        {
            return NarrativeFill.Empty;
        }
        if (fill.SlotText.Count > 0)
        {
            var ttl = TimeSpan.FromHours(Math.Max(1, snapshot.CacheTtlHours));
            _cache.Set(cacheKey, fill, ttl);
        }
        return fill;
    }

    private NarrativeConfigurationSnapshot CaptureConfiguration()
    {
        var source = _configuration;
        var disclosure = source.Disclosure;
        var copiedDisclosure = new AiDisclosurePolicyOptions
        {
            WorkloadDataClassification = disclosure.WorkloadDataClassification,
            Approvals = disclosure.Approvals.Where(rule => rule is not null).Select(rule => new AiDisclosureApproval
            {
                Provider = rule.Provider,
                Kind = rule.Kind,
                Destination = rule.Destination,
                Model = rule.Model,
                UseCase = rule.UseCase,
                DataClassification = rule.DataClassification
            }).ToList()
        };
        return new(source.Enabled, source.Provider, source.Endpoint, source.Model, source.MaxTokens,
            source.ApiKey, source.TimeoutSeconds, source.CacheTtlHours, copiedDisclosure);
    }

    // Secrets stay only in this private invocation snapshot and the authorization header;
    // neither financial fingerprints/receipts nor diagnostics contain this record.
    private sealed record NarrativeConfigurationSnapshot(bool Enabled, string Provider, string Endpoint, string Model,
        int MaxTokens, string ApiKey, int TimeoutSeconds, int CacheTtlHours, AiDisclosurePolicyOptions Disclosure)
    {
        public override string ToString() => "Reporting invocation configuration";
    }

    private static AiFinancialRequest FreezeInvocation(string promptJson, NarrativeConfigurationSnapshot snapshot, AiFinancialPriceBasis? basis)
    {
        var endpoint = AiFinancialPriceBasis.CanonicalDestination(snapshot.Endpoint.TrimEnd('/') + "/chat/completions")
            ?? throw new InvalidOperationException("Reporting narrative destination is invalid.");
        var model = snapshot.Model;
        var output = basis is null ? snapshot.MaxTokens : Math.Min(snapshot.MaxTokens, basis.MaximumOutputTokens);
        var chatRequest = new ReportingOpenAiChatRequest
        {
            Model = model,
            MaxTokens = output,
            ServiceTier = basis?.ServiceTier,
            Temperature = 0.0,
            ResponseFormat = new ReportingOpenAiResponseFormat { Type = "json_object" },
            Messages =
            [
                new ReportingOpenAiMessage { Role = "system", Content = NarrativeSystemPrompt.Default },
                new ReportingOpenAiMessage { Role = "user", Content = promptJson }
            ]
        };
        return new AiFinancialRequest(snapshot.Provider, endpoint, model, output,
            JsonSerializer.SerializeToUtf8Bytes(chatRequest, ReportingJsonContext.Default.ReportingOpenAiChatRequest), basis?.Digest);
    }

    private async Task<NarrativeFill> CallProviderAsync(AiFinancialRequest invocation, NarrativeConfigurationSnapshot snapshot, IAiExecutionPermit permit, CancellationToken cancellationToken)
    {
        // ResponseHeadersRead finishes at headers: retain one timeout budget across
        // authoritative send commitment, HTTP send and the complete response body.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, snapshot.TimeoutSeconds)));
        cancellationToken = timeout.Token;
        await permit.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, snapshot.TimeoutSeconds));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, invocation.Destination);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", snapshot.ApiKey);
        httpRequest.Content = new ByteArrayContent(invocation.CopyBody());
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        await permit.AuthorizeSendAsync(cancellationToken).ConfigureAwait(false);
        using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Error bodies can echo private prompts or credentials. Do not read them
            // into exception messages that may reach reporting fallback diagnostics.
            throw new InvalidOperationException(
                $"Reporting narrative provider returned HTTP {(int)response.StatusCode}.");
        }

        var responseJson = permit.HasFinancialReservation
            ? await ReadFinancialResponseAsync(response.Content, cancellationToken).ConfigureAwait(false)
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (permit.HasFinancialReservation && TryReadFinancialUsage(responseJson, invocation) is { } usage)
            await permit.RecordFinancialUsageAsync(usage, cancellationToken).ConfigureAwait(false);
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

    private static async Task<string> ReadFinancialResponseAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maximumBytes = 2_097_152;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (body.Length + read > maximumBytes)
                throw new InvalidOperationException("Reporting narrative response exceeds the supported financial receipt bound.");
            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return Encoding.UTF8.GetString(body.GetBuffer(), 0, checked((int)body.Length));
    }

    private static AiFinancialUsage? TryReadFinancialUsage(string json, AiFinancialRequest invocation)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        var responseId = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? string.Empty : string.Empty;
        var evidenceDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        AiFinancialUsage Fault()
        {
            var identity = new AiFinancialUsage(invocation.RequestDigest, invocation.Model, responseId, 0, 0, ContractBreachDigest: evidenceDigest, InputTokensKnown: false, OutputTokensKnown: false);
            if (!identity.IsValid) identity = identity with { ProviderResponseId = string.Empty };
            return identity;
        }
        var unsupported = false;
        var contractBreach = false;
        var ambiguousUsage = false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                if (property.Name is "id" or "model" or "service_tier" or "usage") contractBreach = true;
                if (property.Name == "id") responseId = string.Empty;
                if (property.Name == "usage") ambiguousUsage = true;
                unsupported = true;
            }
            if (property.Name is not ("id" or "model" or "choices" or "usage" or "object" or "created" or "system_fingerprint" or "service_tier")) unsupported = true;
        }
        if (!root.TryGetProperty("service_tier", out var tier) || tier.ValueKind != JsonValueKind.String || tier.GetString() != "default") contractBreach = true;
        if (!root.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String || model.GetString() != invocation.Model) contractBreach = true;
        // Root metadata breaches do not erase independently unambiguous usage.
        // Multiple usage objects cannot establish which counter belongs to the send.
        if (ambiguousUsage || !root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return contractBreach ? Fault() : null;
        names.Clear();
        foreach (var property in usage.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                // A duplicate makes only its own counter ambiguous; retain a valid peer.
                unsupported = true;
            }
            if (property.Name is not ("prompt_tokens" or "completion_tokens" or "total_tokens" or "prompt_tokens_details" or "completion_tokens_details")) unsupported = true;
        }
        // Validate unambiguous known counters before applying the closed dimensional
        // schema. Unsupported shape must not mask an observed token/money overrun.
        var overflow = false;
        bool ReadCounter(string name, out long tokens)
        {
            tokens = 0;
            var properties = usage.EnumerateObject().Where(property => property.Name == name).ToArray();
            if (properties.Length != 1 || properties[0].Value.ValueKind != JsonValueKind.Number) return false;
            if (!properties[0].Value.TryGetInt64(out var value) || value > 1_000_000_000_000_000)
            {
                overflow = true;
                return false;
            }
            if (value < 0) return false;
            tokens = value;
            return true;
        }
        var inputKnown = ReadCounter("prompt_tokens", out var inputTokens);
        var outputKnown = ReadCounter("completion_tokens", out var outputTokens);
        unsupported |= !inputKnown || !outputKnown;
        unsupported |= !KnownBasicDetails(usage, "prompt_tokens_details", inputTokens, ["cached_tokens", "cache_write_tokens", "audio_tokens", "image_tokens"])
            || !KnownBasicDetails(usage, "completion_tokens_details", outputTokens, ["reasoning_tokens", "audio_tokens", "accepted_prediction_tokens", "rejected_prediction_tokens"]);
        var contradictory = inputKnown && outputKnown && usage.TryGetProperty("total_tokens", out var total)
            && (total.ValueKind != JsonValueKind.Number || !total.TryGetInt64(out var totalTokens) || totalTokens != inputTokens + outputTokens);
        var receipt = new AiFinancialUsage(invocation.RequestDigest, invocation.Model, responseId, inputTokens, outputTokens,
            ContractBreachDigest: contractBreach ? evidenceDigest : null,
            UnsupportedUsageDigest: unsupported ? evidenceDigest : null, InputTokensKnown: inputKnown, OutputTokensKnown: outputKnown);
        if (!receipt.IsValid) { unsupported = true; receipt = receipt with { ProviderResponseId = string.Empty }; }
        if (unsupported || contradictory) receipt = receipt with { UnsupportedUsageDigest = evidenceDigest };
        if (contradictory) receipt = receipt with { ContractBreachDigest = evidenceDigest };
        if (overflow) receipt = receipt with { UnrepresentableUsageDigest = evidenceDigest };
        return receipt.IsValid ? receipt : null;
    }

    private static bool KnownBasicDetails(JsonElement usage, string name, long total, string[] zeroDimensions)
    {
        if (!usage.TryGetProperty(name, out var details)) return true;
        if (details.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in details.EnumerateObject())
        {
            if (!seen.Add(property.Name) || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out var count)) return false;
            if (property.Name == "text_tokens") { if (count != total) return false; }
            else if (!zeroDimensions.Contains(property.Name, StringComparer.Ordinal) || count != 0) return false;
        }
        return true;
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
