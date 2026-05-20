// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>
/// Narrow, probe-focused contract used by <c>ArcGisMigrationParityRunner</c> to interrogate the
/// Honua-applied target without coupling the runner (or its tests) to the full
/// <see cref="Honua.Core.Features.FeatureStore.Abstractions.IFeatureReader"/> surface.
/// </summary>
/// <remarks>
/// <para>
/// A production adapter is expected to delegate to the catalog-backed
/// <see cref="Honua.Core.Features.FeatureStore.Abstractions.IFeatureReader"/> by resolving the
/// manifest's <see cref="MigrationManifestResourceIdentity.TargetLayerId"/> to the catalog layer
/// id. Tests provide an in-memory implementation so probes can be exercised deterministically.
/// </para>
/// <para>
/// All methods are expected to be cheap, deterministic, and free of side effects. Implementations
/// should never throw for a missing target; instead they should return <c>null</c> so the runner
/// can record an explicit "target not found" probe outcome.
/// </para>
/// </remarks>
public interface IArcGisParityFeatureReader
{
    /// <summary>
    /// Returns the feature count for the supplied target resource id.
    /// </summary>
    /// <param name="targetResourceId">Stable manifest target resource id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Feature count, or <c>null</c> when the target resource was not found.</returns>
    Task<long?> GetFeatureCountAsync(string targetResourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the field names advertised by the supplied target resource.
    /// </summary>
    /// <param name="targetResourceId">Stable manifest target resource id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Field names. Returns an empty array when the target advertises no schema; returns
    /// <c>null</c> when the target resource was not found.
    /// </returns>
    Task<IReadOnlyList<string>?> GetFieldNamesAsync(string targetResourceId, CancellationToken cancellationToken = default);
}
