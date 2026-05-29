// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

/// <summary>
/// Cache policy for an apply run.
/// </summary>
public enum SpecCacheMode
{
    /// <summary>Default: read from cache and write successful outputs.</summary>
    ReadWrite,

    /// <summary>Read from cache but do not write new entries.</summary>
    ReadOnly,

    /// <summary>Ignore the cache entirely and recompute every node.</summary>
    Bypass
}

/// <summary>
/// Options for an apply run.
/// </summary>
public sealed record SpecApplyOptions
{
    /// <summary>Cache policy. Defaults to <see cref="SpecCacheMode.ReadWrite"/>.</summary>
    public SpecCacheMode CacheMode { get; init; } = SpecCacheMode.ReadWrite;

    /// <summary>Maximum nodes running concurrently. Defaults to 4.</summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>
    /// TTL stamped onto cache entries whose planned node carries
    /// <c>mutable-source-no-pin</c>. Defaults to 15 minutes per the
    /// documented S1 contract; null disables TTL degradation entirely.
    /// </summary>
    public TimeSpan? MutableSourceTtl { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Orchestrates an apply run over a canonical spec. Emits events describing
/// per-node progress; the same event stream drives both SSE and gRPC surfaces.
/// </summary>
public interface ISpecApplyEngine
{
    /// <summary>
    /// Starts an apply run and returns the apply token plus a stream of
    /// events. The returned stream completes when the apply reaches a
    /// terminal state (completed, cancelled, or failed at the document level).
    /// </summary>
    /// <param name="document">Canonical spec to apply.</param>
    /// <param name="options">Apply options.</param>
    /// <param name="cancellationToken">Request-scoped cancellation token.</param>
    Task<SpecApplyHandle> StartAsync(
        CanonicalSpecDocument document,
        SpecApplyOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cooperatively cancels an in-flight apply by token. Returns <c>true</c>
    /// when the token was resolved; <c>false</c> when the token is unknown.
    /// </summary>
    bool TryCancel(string applyToken);
}

/// <summary>
/// Handle returned by <see cref="ISpecApplyEngine.StartAsync"/>.
/// </summary>
public sealed class SpecApplyHandle
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SpecApplyHandle"/> class.
    /// </summary>
    public SpecApplyHandle(string applyToken, IAsyncEnumerable<SpecApplyEvent> events, SpecPlan plan)
    {
        ApplyToken = applyToken;
        Events = events;
        Plan = plan;
    }

    /// <summary>Opaque apply identifier used by <c>POST /v1/spec/cancel</c>.</summary>
    public string ApplyToken { get; }

    /// <summary>The plan that is being applied.</summary>
    public SpecPlan Plan { get; }

    /// <summary>
    /// Stream of apply events. Hot, single-consumer stream backed by a bounded
    /// channel: the run is already executing when the handle is returned, so
    /// events may have been produced before enumeration begins. Only one
    /// consumer may read the stream — a second enumeration will observe the
    /// tail of the first reader or an empty completed channel.
    /// </summary>
    public IAsyncEnumerable<SpecApplyEvent> Events { get; }
}
