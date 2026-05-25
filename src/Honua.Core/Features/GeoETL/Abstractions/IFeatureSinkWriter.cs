// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using NetTopologySuite.Features;

namespace Honua.Core.Features.GeoETL.Abstractions;

/// <summary>
/// Provider-neutral seam for the Honua-layer / PostGIS sink. The Honua.Postgres
/// implementation mirrors the <c>StreamingFileImportService</c> insert path
/// (<c>honua.create_import_table</c> + <c>honua.insert_import_feature</c>), keeping
/// Honua.Core free of Npgsql so the GeoETL slice stays on the lean managed path.
/// </summary>
/// <remarks>
/// This abstraction is the reuse boundary called out in the roadmap ("Honua-layer
/// sink reuses StreamingFileImportService insert path"). The sink connector lives in
/// Honua.Core and depends only on this seam; the durable insert lives in Honua.Postgres.
/// </remarks>
public interface IFeatureSinkWriter
{
    /// <summary>
    /// Ensures the target table exists for the given schema / table / SRID, creating it
    /// through the canonical import-table function when absent.
    /// </summary>
    /// <param name="schema">Target schema name.</param>
    /// <param name="table">Target table name.</param>
    /// <param name="targetSrid">SRID the geometry column is stored in.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task EnsureTargetAsync(
        string schema,
        string table,
        int targetSrid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a batch of features into the target table, tagging each row with
    /// <paramref name="batchId"/> for soft-delete rollback. Geometries are reprojected
    /// from <paramref name="sourceSrid"/> to the table SRID by the insert function.
    /// </summary>
    /// <param name="schema">Target schema name.</param>
    /// <param name="table">Target table name.</param>
    /// <param name="features">Features to insert.</param>
    /// <param name="sourceSrid">SRID of the incoming geometries.</param>
    /// <param name="targetSrid">SRID the geometry column is stored in.</param>
    /// <param name="batchId">Batch identifier tagged on every inserted row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of rows inserted (features with null geometry are skipped).</returns>
    Task<long> InsertBatchAsync(
        string schema,
        string table,
        IReadOnlyList<IFeature> features,
        int sourceSrid,
        int targetSrid,
        string batchId,
        CancellationToken cancellationToken = default);
}
