// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.StudioAiProxy.Domain;

namespace Honua.Ai.StudioAiProxy.Abstractions;

/// <summary>
/// One adapter translates the provider-neutral <see cref="StudioAiChatRequest"/> /
/// <see cref="StudioAiChatEvent"/> contract to and from a specific upstream wire protocol. There
/// are exactly three in v0 — <see cref="StudioAiProxyConfiguration.AnthropicKind"/>,
/// <see cref="StudioAiProxyConfiguration.OpenAiKind"/>, <see cref="StudioAiProxyConfiguration.BedrockKind"/>
/// — selected per call by <see cref="StudioAiProxyProviderOptions.Kind"/>, not by the operator-named
/// provider (many named providers can share one kind, e.g. two different OpenAI-compatible
/// endpoints).
/// </summary>
public interface IStudioAiProxyAdapter
{
    /// <summary>The adapter kind this instance implements (matches <see cref="StudioAiProxyProviderOptions.Kind"/>).</summary>
    string Kind { get; }

    /// <summary>
    /// Whether <paramref name="options"/> has everything this adapter needs to place a call
    /// (model id, and credentials the adapter can resolve without a network round trip's worth of
    /// blocking — actual key resolution happens inside <see cref="StreamAsync"/>).
    /// </summary>
    bool IsConfigured(StudioAiProxyProviderOptions options);

    /// <summary>
    /// Streams one chat turn against the upstream provider named by <paramref name="options"/>,
    /// translating provider-native streaming frames into the neutral <see cref="StudioAiChatEvent"/>
    /// sequence. Implementations must honor <paramref name="cancellationToken"/> promptly (request
    /// abort / client disconnect) and must terminate the sequence with exactly one event whose
    /// <see cref="StudioAiChatEvent.Type"/> is <see cref="StudioAiChatEventType.MessageStop"/> or
    /// <see cref="StudioAiChatEventType.Error"/> — never both, and never neither.
    /// </summary>
    IAsyncEnumerable<StudioAiChatEvent> StreamAsync(
        StudioAiProxyProviderOptions options,
        StudioAiChatRequest request,
        CancellationToken cancellationToken);
}
