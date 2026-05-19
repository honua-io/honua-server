// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>
/// Sink that materializes projected OGC API Features into a target catalog table.
/// Implementations are responsible for idempotent writes so that re-running an import on the
/// same source/target combination converges to the same row set.
/// </summary>
public interface IOgcApiFeaturesCollectionSink
{
    /// <summary>
    /// Ensures the target table exists, creating it when missing. Implementations may also
    /// upgrade an existing target table by adding missing columns when it is safe to do so.
    /// </summary>
    /// <param name="target">Sink target descriptor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task EnsureTargetAsync(OgcApiFeaturesSinkTarget target, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a batch of features to the target. The implementation must upsert on the natural
    /// source identifier so re-runs do not duplicate rows.
    /// </summary>
    /// <param name="target">Sink target descriptor.</param>
    /// <param name="features">Batch of feature payloads.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of features written to the target.</returns>
    Task<int> WriteFeaturesAsync(
        OgcApiFeaturesSinkTarget target,
        IReadOnlyList<OgcApiFeaturesSinkFeature> features,
        CancellationToken cancellationToken);
}

/// <summary>
/// Descriptor for the OGC API Features collection sink target.
/// </summary>
public sealed record OgcApiFeaturesSinkTarget
{
    /// <summary>
    /// Target schema. Implementations MUST treat this as untrusted input and quote it before
    /// emitting DDL or DML.
    /// </summary>
    public required string Schema { get; init; }

    /// <summary>
    /// Target table. Implementations MUST treat this as untrusted input and quote it before
    /// emitting DDL or DML.
    /// </summary>
    public required string Table { get; init; }

    /// <summary>
    /// Stable identifier of the source collection (used in warnings and provenance metadata).
    /// </summary>
    public required string CollectionId { get; init; }

    /// <summary>
    /// Storage SRID for the geometry column. Defaults to 4326 to match OGC API Features defaults.
    /// </summary>
    public int Srid { get; init; } = 4326;
}

/// <summary>
/// Single OGC API Features feature payload, projected for sink ingestion.
/// </summary>
public sealed record OgcApiFeaturesSinkFeature
{
    /// <summary>
    /// Source-provided feature identifier. Used as the upsert key.
    /// </summary>
    public required string SourceFeatureId { get; init; }

    /// <summary>
    /// GeoJSON geometry payload, or <c>null</c> if the source feature did not declare one.
    /// </summary>
    public string? GeoJsonGeometry { get; init; }

    /// <summary>
    /// GeoJSON-encoded feature properties, serialized as a JSON object literal. Empty objects
    /// are persisted as <c>{}</c>.
    /// </summary>
    public required string PropertiesJson { get; init; }
}
