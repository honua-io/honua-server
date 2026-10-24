// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Geoprocessing.Release;

namespace Honua.Geoprocessing.ProcessRelease;

/// <summary>
/// Immutable release records plus the versioned per-process binding (honua-server#6221).
/// Publishing is idempotent by digest; activation is an atomic compare-and-set on the
/// binding version so two concurrent deployments cannot both believe they won.
/// </summary>
public interface IGpProcessReleaseStore
{
    /// <summary>
    /// Stores a lock as an immutable record. Publishing a digest that already exists returns
    /// the existing record unchanged (same bytes, same first publication time).
    /// </summary>
    Task<GpProcessReleaseRecord> PublishAsync(GpProcessReleaseLock lockDocument, string publishedBy, CancellationToken cancellationToken = default);

    /// <summary>Publishes atomically and reports whether this call created the immutable record.</summary>
    Task<(GpProcessReleaseRecord Record, bool Created)> PublishWithStatusAsync(GpProcessReleaseLock lockDocument, string publishedBy, CancellationToken cancellationToken = default);

    /// <summary>Reads a published release by digest.</summary>
    Task<GpProcessReleaseRecord?> GetReleaseAsync(string digest, CancellationToken cancellationToken = default);

    /// <summary>Lists published releases, newest first, optionally for one process.</summary>
    Task<IReadOnlyList<GpProcessReleaseRecord>> ListReleasesAsync(string? process, CancellationToken cancellationToken = default);

    /// <summary>Reads the live binding for a process; null when it was never bound.</summary>
    Task<GpProcessBinding?> GetBindingAsync(string process, CancellationToken cancellationToken = default);

    /// <summary>Lists every live binding.</summary>
    Task<IReadOnlyList<GpProcessBinding>> ListBindingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates <paramref name="activation"/> against the published lock, the live binding and
    /// <paramref name="environment"/> under the store's concurrency guard, and when admissible
    /// writes the new binding with <c>bindingVersion = expected + 1</c>. Every refusal is
    /// returned, never thrown, so a deployment can record it in its receipt.
    /// </summary>
    Task<GpProcessBindingDecision> ActivateAsync(GpProcessBindingActivation activation, GpProcessReleaseEnvironment environment, CancellationToken cancellationToken = default);
}
