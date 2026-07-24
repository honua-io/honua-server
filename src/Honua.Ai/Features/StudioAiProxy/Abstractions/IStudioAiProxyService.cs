// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.StudioAiProxy.Domain;

namespace Honua.Ai.StudioAiProxy.Abstractions;

/// <summary>
/// Orchestrates the Studio AI proxy: resolves the named (or default) provider, picks the adapter for
/// its <see cref="StudioAiProxyProviderOptions.Kind"/>, and streams the turn. This is the only
/// service the endpoint layer talks to; it never talks to an upstream provider directly.
/// </summary>
public interface IStudioAiProxyService
{
    /// <summary>Whether the feature is enabled and at least one provider is fully configured.</summary>
    bool Enabled { get; }

    /// <summary>Returns the capability descriptors for every declared provider (REQ-003).</summary>
    Task<StudioAiCapabilitiesResponse> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves <see cref="StudioAiChatRequest.Provider"/> (or the configured default) and validates
    /// that it names a known, configured provider. Returns <see langword="null"/> on success (the
    /// caller may proceed to <see cref="StreamChatAsync"/>) or a caller-facing rejection reason.
    /// Split out from <see cref="StreamChatAsync"/> so the endpoint can return a normal 400 JSON
    /// problem for a bad request instead of committing SSE response headers first (issue: SSE has no
    /// way to change the HTTP status after the first byte is written).
    /// </summary>
    string? ValidateRequest(StudioAiChatRequest request);

    /// <summary>
    /// Streams one chat turn. <paramref name="summary"/> is populated in place as the stream
    /// progresses (provider/kind/model immediately; usage, stop reason, and outcome once the terminal
    /// event has been produced) so a caller that has already committed to a streaming HTTP response
    /// can still build one audit record after the <c>await foreach</c> loop completes. Call
    /// <see cref="ValidateRequest"/> first — this method assumes the request already resolves to a
    /// known, configured provider.
    /// </summary>
    IAsyncEnumerable<StudioAiChatEvent> StreamChatAsync(
        StudioAiChatRequest request,
        StudioAiProxyCallSummary summary,
        CancellationToken cancellationToken);
}

/// <summary>
/// Mutable summary of one proxied call, filled in by <see cref="IStudioAiProxyService.StreamChatAsync"/>
/// as the stream progresses. Read after the stream completes (or aborts) to build the audit record —
/// token counts and the final outcome are only known once the terminal event has been produced.
/// </summary>
public sealed class StudioAiProxyCallSummary
{
    /// <summary>Resolved provider name.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Resolved adapter kind.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Resolved model id (request override or provider default).</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Prompt tokens reported by the provider, when available.</summary>
    public int? PromptTokens { get; set; }

    /// <summary>Completion tokens reported by the provider, when available.</summary>
    public int? CompletionTokens { get; set; }

    /// <summary>Wall-clock time from dispatch to the terminal event.</summary>
    public long LatencyMs { get; set; }

    /// <summary>Terminal stop reason, once known.</summary>
    public StudioAiStopReason? StopReason { get; set; }

    /// <summary>Whether the call reached a normal <see cref="StudioAiChatEventType.MessageStop"/> (as opposed to an error).</summary>
    public bool Succeeded { get; set; }

    /// <summary>Failure detail when <see cref="Succeeded"/> is false.</summary>
    public string? ErrorMessage { get; set; }
}
