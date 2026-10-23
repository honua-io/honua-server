// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Honua.Ai.StudioAiProxy.Abstractions;
using Honua.Ai.StudioAiProxy.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Ai.StudioAiProxy;

/// <summary>
/// Orchestrates the Studio AI proxy: resolves the named (or default) configured provider, dispatches
/// to the adapter registered for its <see cref="StudioAiProxyProviderOptions.Kind"/>, and — as a
/// defense-in-depth backstop over the adapter contract — guarantees the event sequence always ends
/// with exactly one terminal event even if an adapter misbehaves (throws instead of yielding an
/// <see cref="StudioAiChatEventType.Error"/>).
/// </summary>
internal sealed class StudioAiProxyService : IStudioAiProxyService
{
    private const int MaxToolCount = 128;
    private const int MaxMessageCount = 256;
    private const int MaxToolCallCount = 256;
    private const int MaxToolComponentCharacters = 64_000;
    private const int MaxTranscriptEventCount = 4_096;
    private const long MaxTranscriptCharacters = 1_000_000;

    private readonly StudioAiProxyConfiguration _configuration;
    private readonly Dictionary<string, IStudioAiProxyAdapter> _adaptersByKind;
    private readonly ILogger<StudioAiProxyService> _logger;
    private readonly StudioAiTranscriptSigner _transcriptSigner;

    public StudioAiProxyService(
        IOptions<StudioAiProxyConfiguration> options,
        IEnumerable<IStudioAiProxyAdapter> adapters,
        StudioAiTranscriptSigner transcriptSigner,
        ILogger<StudioAiProxyService> logger)
    {
        _configuration = options.Value;
        _adaptersByKind = adapters
            .GroupBy(a => a.Kind, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _logger = logger;
        _transcriptSigner = transcriptSigner;
    }

    public bool Enabled => _configuration.Enabled && _configuration.Providers.Count > 0;

    public async Task<StudioAiCapabilitiesResponse> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        if (!_configuration.Enabled)
        {
            return new StudioAiCapabilitiesResponse { Enabled = false };
        }

        var providers = _configuration.Providers
            .Select(kv => BuildCapability(kv.Key, kv.Value))
            .OrderBy(p => p.Provider, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new StudioAiCapabilitiesResponse
        {
            Enabled = true,
            DefaultProvider = _configuration.DefaultProvider,
            Providers = providers,
            TranscriptSigning = await _transcriptSigner.GetManifestAsync(cancellationToken).ConfigureAwait(false)
        };
    }

    public string? ValidateRequest(StudioAiChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_configuration.Enabled)
        {
            return "The Studio AI proxy is not enabled.";
        }

        if (request.Messages is not { Count: > 0 })
        {
            return "At least one message is required.";
        }

        if (request.Messages.Count > MaxMessageCount)
        {
            return $"A maximum of {MaxMessageCount} messages is allowed per request.";
        }

        if (request.Messages.Any(static message => message is null || message.Content is null))
        {
            return "Message content must not be null.";
        }

        if (request.Certification is not null)
        {
            var certification = request.Certification;
            if (string.IsNullOrWhiteSpace(certification.CandidateId)
                || string.IsNullOrWhiteSpace(certification.ReleaseId)
                || string.IsNullOrWhiteSpace(certification.TenantId)
                || string.IsNullOrWhiteSpace(certification.EndpointIdentity)
                || string.IsNullOrWhiteSpace(certification.ActionId)
                || string.IsNullOrWhiteSpace(certification.RunNonce))
            {
                return "Certification requires candidateId, tenantId, releaseId, endpointIdentity, actionId, and runNonce.";
            }
        }

        if (request.Tools is { Count: > MaxToolCount })
        {
            return $"A maximum of {MaxToolCount} tools is allowed per request.";
        }

        long totalChars = request.Messages.Sum(m => (long)m.Content.Length) + (request.System?.Length ?? 0);
        var totalToolCalls = 0;
        if (request.ToolChoice is not null)
        {
            totalChars += request.ToolChoice.ToolName?.Length ?? 0;
            totalChars += request.ToolChoice.Mode.ToString().Length;
        }

        if (request.Tools is { Count: > 0 } tools)
        {
            foreach (var tool in tools)
            {
                var toolSchemaCharacters = JsonCharacterCount(tool.InputSchema);
                if (toolSchemaCharacters > MaxToolComponentCharacters)
                {
                    return $"Tool '{tool.Name}' input schema exceeds the configured per-tool limit of {MaxToolComponentCharacters} characters.";
                }

                var annotationCharacters = JsonCharacterCount(tool.Annotations);
                if (annotationCharacters > MaxToolComponentCharacters)
                {
                    return $"Tool '{tool.Name}' annotations exceed the configured per-tool limit of {MaxToolComponentCharacters} characters.";
                }

                var outputSchemaCharacters = JsonCharacterCount(tool.OutputSchema);
                if (outputSchemaCharacters > MaxToolComponentCharacters)
                {
                    return $"Tool '{tool.Name}' output schema exceeds the configured per-tool limit of {MaxToolComponentCharacters} characters.";
                }

                totalChars += tool.Name.Length;
                totalChars += tool.Description?.Length ?? 0;
                totalChars += toolSchemaCharacters;
                totalChars += annotationCharacters;
                totalChars += outputSchemaCharacters;
            }
        }

        foreach (var message in request.Messages)
        {
            totalChars += message.ToolCallId?.Length ?? 0;
            totalChars += message.ToolName?.Length ?? 0;
            if (message.ToolCalls is { Count: > 0 } toolCalls)
            {
                totalToolCalls += toolCalls.Count;
                if (totalToolCalls > MaxToolCallCount)
                {
                    return $"A maximum of {MaxToolCallCount} assistant tool calls is allowed per request.";
                }

                foreach (var toolCall in toolCalls)
                {
                    var argumentCharacters = JsonCharacterCount(toolCall.Arguments);
                    if (argumentCharacters > MaxToolComponentCharacters)
                    {
                        return $"Tool call '{toolCall.Name}' arguments exceed the configured per-call limit of {MaxToolComponentCharacters} characters.";
                    }

                    totalChars += toolCall.Id.Length;
                    totalChars += toolCall.Name.Length;
                    totalChars += argumentCharacters;
                }
            }
        }

        if (totalChars > _configuration.MaxPromptCharacters)
        {
            return $"Request content exceeds the configured limit of {_configuration.MaxPromptCharacters} characters.";
        }

        if (request.ToolChoice?.Mode == StudioAiToolChoiceMode.Specific && string.IsNullOrWhiteSpace(request.ToolChoice.ToolName))
        {
            return "toolChoice.mode 'specific' requires toolChoice.toolName.";
        }

        var providerName = ResolveProviderName(request);
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return "No provider was named and no default provider is configured.";
        }

        var providerOptions = _configuration.GetProvider(providerName);
        if (providerOptions is null)
        {
            return $"Provider '{providerName}' is not configured.";
        }

        if (!_adaptersByKind.TryGetValue(providerOptions.Kind, out var adapter))
        {
            return $"Provider '{providerName}' declares unsupported kind '{providerOptions.Kind}'.";
        }

        if (!adapter.IsConfigured(providerName, providerOptions))
        {
            return $"Provider '{providerName}' is missing required configuration (model or credentials).";
        }

        if (request.Tools is { Count: > 0 } && !providerOptions.SupportsTools)
        {
            return $"Provider '{providerName}' does not support tool calls.";
        }

        return null;
    }

    private static int JsonCharacterCount(JsonElement value)
        => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? 0
            : value.GetRawText().Length;

    private static int JsonCharacterCount(JsonElement? value)
        => value is null ? 0 : JsonCharacterCount(value.Value);

    public async IAsyncEnumerable<StudioAiChatEvent> StreamChatAsync(
        StudioAiChatRequest request,
        StudioAiProxyCallSummary summary,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(summary);

        var providerName = ResolveProviderName(request)!;
        var providerOptions = _configuration.GetProvider(providerName)!;
        var adapter = _adaptersByKind[providerOptions.Kind];
        var model = string.IsNullOrWhiteSpace(request.Model) ? providerOptions.Model : request.Model!;

        summary.Provider = providerName;
        summary.Kind = providerOptions.Kind;
        summary.Model = model;

        StudioAiProxyLog.ChatRequested(_logger, providerName, providerOptions.Kind, model);

        StudioAiTranscriptSigner.SigningKey? signingKey = null;
        if (request.Certification is not null)
        {
            signingKey = await _transcriptSigner.ResolveKeyAsync(cancellationToken).ConfigureAwait(false);
            if (signingKey is null)
            {
                summary.Succeeded = false;
                summary.StopReason = StudioAiStopReason.Error;
                summary.ErrorMessage = "Transcript provenance signing is unavailable.";
                yield return new StudioAiChatEvent
                {
                    Type = StudioAiChatEventType.Error,
                    Model = model,
                    ErrorCode = StudioAiTranscriptSigner.UnavailableCode,
                    ErrorMessage = "Transcript provenance signing is unavailable."
                };
                yield break;
            }
        }

        var enumerator = adapter.StreamAsync(providerOptions, request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var validator = new StudioAiStreamGrammarValidator();
        StudioAiChatEvent? successfulTerminal = null;
        var transcriptEvents = signingKey is null ? null : new List<StudioAiChatEvent>();
        long transcriptCharacters = 0;
        long responseBytes = 0;
        var responseEventCount = 0;
        var toolArgumentBytes = new Dictionary<string, long>(StringComparer.Ordinal);
        string? providerReportedModel = null;

        try
        {
            while (true)
            {
                var (hasNext, failure) = await MoveNextSafeAsync(enumerator, cancellationToken).ConfigureAwait(false);
                if (failure is not null)
                {
                    summary.Succeeded = false;
                    summary.ErrorMessage = "Adapter failed unexpectedly.";
                    summary.StopReason = StudioAiStopReason.Error;
                    StudioAiProxyLog.ProviderRequestFailed(_logger, providerName, failure);
                    yield return new StudioAiChatEvent
                    {
                        Type = StudioAiChatEventType.Error,
                        Model = model,
                        ErrorCode = StudioAiStreamGrammarValidator.InvalidStreamCode,
                        ErrorMessage = "The provider adapter failed unexpectedly."
                    };
                    yield break;
                }

                if (!hasNext)
                {
                    break;
                }

                var evt = enumerator.Current;
                var eventBytes = JsonSerializer.SerializeToUtf8Bytes(
                    evt,
                    StudioAiProxyJsonContext.Default.StudioAiChatEvent).Length;
                responseBytes += eventBytes;
                responseEventCount++;
                if (evt.Type == StudioAiChatEventType.ToolCallDelta && evt.ToolArgumentsDelta is { } argumentDelta)
                {
                    var toolCallId = evt.ToolCallId ?? string.Empty;
                    toolArgumentBytes.TryGetValue(toolCallId, out var accumulatedBytes);
                    toolArgumentBytes[toolCallId] = accumulatedBytes + Encoding.UTF8.GetByteCount(argumentDelta);
                }

                if (eventBytes > _configuration.MaxEventBytes
                    || responseBytes > _configuration.MaxResponseBytes
                    || responseEventCount > _configuration.MaxResponseEventCount
                    || toolArgumentBytes.Values.Any(bytes => bytes > _configuration.MaxToolArgumentBytes))
                {
                    summary.Succeeded = false;
                    summary.StopReason = StudioAiStopReason.Error;
                    summary.ErrorMessage = "Provider output exceeded a configured byte limit.";
                    yield return new StudioAiChatEvent
                    {
                        Type = StudioAiChatEventType.Error,
                        Model = model,
                        ErrorCode = "studio_ai/provider_output_too_large",
                        ErrorMessage = "Provider output exceeded a configured byte limit."
                    };
                    yield break;
                }

                if (validator.Validate(evt) is { } rejectionReason)
                {
                    summary.Succeeded = false;
                    summary.StopReason = StudioAiStopReason.Error;
                    summary.ErrorMessage = "Provider returned an invalid event stream.";
                    yield return new StudioAiChatEvent
                    {
                        Type = StudioAiChatEventType.Error,
                        Model = model,
                        ErrorCode = StudioAiStreamGrammarValidator.InvalidStreamCode,
                        ErrorMessage = $"Provider stream rejected: {rejectionReason}."
                    };
                    yield break;
                }

                if (transcriptEvents is not null)
                {
                    transcriptCharacters += EventCharacterCount(evt);
                    if (transcriptEvents.Count >= MaxTranscriptEventCount || transcriptCharacters > MaxTranscriptCharacters)
                    {
                        summary.Succeeded = false;
                        summary.StopReason = StudioAiStopReason.Error;
                        summary.ErrorMessage = "Certification transcript exceeded the capture limit.";
                        yield return new StudioAiChatEvent
                        {
                            Type = StudioAiChatEventType.Error,
                            Model = model,
                            ErrorCode = "studio_ai/provenance_transcript_too_large",
                            ErrorMessage = "Certification transcript exceeded the capture limit."
                        };
                        yield break;
                    }

                    transcriptEvents.Add(evt);
                    if (evt.Type == StudioAiChatEventType.MessageStart && !string.IsNullOrWhiteSpace(evt.Model))
                    {
                        providerReportedModel = evt.Model;
                    }
                }
                if (evt.Type == StudioAiChatEventType.MessageStop)
                {
                    // Hold successful termination until EOF. This makes duplicate and post-terminal
                    // events ineligible for both messageStop and provenance signing.
                    successfulTerminal = evt;
                    continue;
                }

                yield return evt;
                if (evt.Type == StudioAiChatEventType.Error)
                {
                    ApplySummary(summary, providerName, evt);
                    yield break;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        if (successfulTerminal is null)
        {
            // Contract violation guard: an adapter that ends its sequence without a terminal event
            // (see IStudioAiProxyAdapter remarks) still gets exactly one Error event and one audit
            // record here rather than leaving the client's stream hanging silently.
            summary.Succeeded = false;
            summary.ErrorMessage = "Adapter ended without a terminal event.";
            summary.StopReason = StudioAiStopReason.Error;
            yield return new StudioAiChatEvent
            {
                Type = StudioAiChatEventType.Error,
                Model = model,
                ErrorCode = StudioAiStreamGrammarValidator.InvalidStreamCode,
                ErrorMessage = "The provider adapter ended the stream unexpectedly."
            };
            yield break;
        }

        if (signingKey is not null)
        {
            if (string.IsNullOrWhiteSpace(providerReportedModel))
            {
                summary.Succeeded = false;
                summary.StopReason = StudioAiStopReason.Error;
                summary.ErrorMessage = "The provider did not report the model used; certification is unavailable.";
                yield return new StudioAiChatEvent
                {
                    Type = StudioAiChatEventType.Error,
                    Model = model,
                    ErrorCode = StudioAiTranscriptSigner.UnavailableCode,
                    ErrorMessage = "The provider did not report the model used; certification is unavailable."
                };
                yield break;
            }

            StudioAiSignedTranscript? provenance = null;
            try
            {
                provenance = _transcriptSigner.Sign(
                    signingKey, request, providerName, providerReportedModel, transcriptEvents!);
            }
            catch (InvalidOperationException)
            {
                summary.Succeeded = false;
                summary.StopReason = StudioAiStopReason.Error;
                summary.ErrorMessage = "Transcript provenance validation failed.";
            }

            if (provenance is null)
            {
                yield return new StudioAiChatEvent
                {
                    Type = StudioAiChatEventType.Error,
                    Model = model,
                    ErrorCode = "studio_ai/provenance_validation_failed",
                    ErrorMessage = "Transcript provenance validation failed."
                };
                yield break;
            }

            var provenanceEvent = new StudioAiChatEvent
            {
                Type = StudioAiChatEventType.TranscriptProvenance,
                Provenance = provenance
            };
            var provenanceBytes = JsonSerializer.SerializeToUtf8Bytes(
                provenanceEvent,
                StudioAiProxyJsonContext.Default.StudioAiChatEvent).Length;
            if (provenanceBytes > _configuration.MaxEventBytes
                || responseBytes + provenanceBytes > _configuration.MaxResponseBytes
                || responseEventCount + 1 > _configuration.MaxResponseEventCount)
            {
                summary.Succeeded = false;
                summary.StopReason = StudioAiStopReason.Error;
                summary.ErrorMessage = "Transcript provenance exceeded a configured response limit.";
                yield return new StudioAiChatEvent
                {
                    Type = StudioAiChatEventType.Error,
                    Model = model,
                    ErrorCode = "studio_ai/provider_output_too_large",
                    ErrorMessage = "Transcript provenance exceeded a configured response limit."
                };
                yield break;
            }

            ApplySummary(summary, providerName, successfulTerminal);
            yield return successfulTerminal;
            yield return provenanceEvent;
            yield break;
        }

        ApplySummary(summary, providerName, successfulTerminal);
        yield return successfulTerminal;
    }

    private static long EventCharacterCount(StudioAiChatEvent evt)
        => (evt.Model?.Length ?? 0L)
            + (evt.Text?.Length ?? 0L)
            + (evt.ToolCallId?.Length ?? 0L)
            + (evt.ToolName?.Length ?? 0L)
            + (evt.ToolArgumentsDelta?.Length ?? 0L)
            + (evt.ToolArguments?.GetRawText().Length ?? 0L)
            + (evt.ErrorMessage?.Length ?? 0L)
            + (evt.ErrorCode?.Length ?? 0L);

    private void ApplySummary(StudioAiProxyCallSummary summary, string providerName, StudioAiChatEvent evt)
    {
        summary.LatencyMs = evt.LatencyMs ?? summary.LatencyMs;

        if (evt.Type == StudioAiChatEventType.MessageStop)
        {
            summary.PromptTokens = evt.PromptTokens;
            summary.CompletionTokens = evt.CompletionTokens;
            summary.StopReason = evt.StopReason;
            summary.Succeeded = true;
            StudioAiProxyLog.ChatCompleted(_logger, providerName, evt.StopReason ?? StudioAiStopReason.EndTurn, summary.LatencyMs);
        }
        else
        {
            summary.Succeeded = false;
            summary.ErrorMessage = evt.ErrorMessage;
            summary.StopReason = StudioAiStopReason.Error;
        }
    }

    private static async Task<(bool HasNext, Exception? Failure)> MoveNextSafeAsync(
        IAsyncEnumerator<StudioAiChatEvent> enumerator,
        CancellationToken callerToken)
    {
        try
        {
            var hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            return (hasNext, null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentional catch-all: this is the boundary over a plugged-in IStudioAiProxyAdapter, which
        // per its own contract is expected to translate its provider's failures into Error events
        // rather than throw; this is the backstop for an adapter that does not honor that contract.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (false, ex);
        }
    }

    private string? ResolveProviderName(StudioAiChatRequest request)
        => string.IsNullOrWhiteSpace(request.Provider) ? _configuration.DefaultProvider : request.Provider;

    private StudioAiCapability BuildCapability(string name, StudioAiProxyProviderOptions options)
    {
        var configured = _adaptersByKind.TryGetValue(options.Kind, out var adapter) && adapter.IsConfigured(name, options);
        return new StudioAiCapability
        {
            Provider = name,
            Kind = options.Kind,
            Model = options.Model,
            MaxTokens = options.MaxTokens,
            ToolSupport = options.SupportsTools,
            Streaming = true,
            IsDefault = string.Equals(name, _configuration.DefaultProvider, StringComparison.OrdinalIgnoreCase),
            Configured = configured
        };
    }
}
