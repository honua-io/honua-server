// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Core.Features.Orchestration.Abstractions;

/// <summary>
/// Probes a watched object store for new objects under a prefix. The baseline
/// implementation polls a configured store and reports the newest object marker
/// (key + last-modified) so the scheduler can fire when the marker advances past the
/// durable object-store cursor. The contract is intentionally push-friendly: an
/// S3/event-driven implementation can surface the same <see cref="ObjectStoreProbeResult"/>
/// without changing the scheduler dispatch.
/// </summary>
public interface IObjectStoreTriggerProbe
{
    /// <summary>
    /// Returns the newest object currently visible under the configured store and prefix,
    /// or <c>null</c> when the store/prefix is empty or the store id is not registered.
    /// </summary>
    Task<ObjectStoreProbeResult?> ProbeNewestAsync(
        ObjectStoreTriggerConfig config,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Marker identifying the newest object observed under a watched prefix. The
/// <see cref="Marker"/> is a monotonic, lexicographically-comparable string so the
/// scheduler can compare it against the durable cursor without store-specific knowledge.
/// </summary>
public readonly record struct ObjectStoreProbeResult
{
    /// <summary>
    /// Opaque key of the newest object (e.g. an S3 key or relative file path).
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Last-modified time of the newest object.
    /// </summary>
    public required DateTimeOffset LastModified { get; init; }

    /// <summary>
    /// Monotonic, comparable marker for the newest object. New objects must produce a marker
    /// that sorts strictly after every previously-observed object's marker. Composed from the
    /// last-modified timestamp and key so ties (same mtime) remain deterministic.
    /// </summary>
    public string Marker => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{LastModified.ToUniversalTime():O}|{Key}");
}
