// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

/// <summary>
/// CRS detection service that never recognizes anything. Registered by read-only feature
/// providers (DuckDB, MySQL/MariaDB) so DI activation succeeds for
/// <c>Honua.Infrastructure.Services.SpatialReferenceResolver</c> — a mandatory, scoped
/// dependency of the FeatureServer, ImageServer, GeometryService, and gRPC protocol
/// adapters regardless of the active data-source provider.
/// </summary>
/// <remarks>
/// <para>
/// Found and fixed under honua-server#2947 (secondary-provider HTTP-stack GA proof): with
/// no <see cref="ICrsDetectionService"/> registration at all, any request that resolved
/// <c>SpatialReferenceResolver</c> under <c>DataSource:Provider=duckdb</c> or <c>mysql</c>
/// failed DI activation outright (<c>InvalidOperationException: Unable to resolve service
/// for type 'ICrsDetectionService'</c>) — breaking every FeatureServer query, not just ones
/// that pass a named/WKT spatial reference. Only <c>Honua.Postgres</c> ever registered an
/// implementation, because CRS detection from WKT/.prj/GeoJSON content genuinely depends on
/// Postgres's <c>spatial_ref_sys</c> catalog.
/// </para>
/// <para>
/// This stub is intentionally honest, not a workaround: the common paths (a plain numeric
/// SRID or an <c>EPSG:nnnn</c> string) are already resolved by
/// <c>SpatialReferenceHelpers.TryParseSrid</c> before
/// <see cref="ICrsDetectionService"/> is ever consulted. Only named aliases (e.g.
/// <c>"WGS84"</c>) and raw WKT/.prj/GeoJSON-CRS content fall through to this service, and
/// DuckDB/MySQL genuinely have no CRS catalog to detect those against — reporting "not
/// recognized" is the correct, capability-scoped answer, not a fabricated one.
/// </para>
/// </remarks>
public sealed class NoOpCrsDetectionService : ICrsDetectionService
{
    /// <inheritdoc />
    public Task<int?> DetectFromPrjAsync(string prjContent, CancellationToken cancellationToken = default)
        => Task.FromResult<int?>(null);

    /// <inheritdoc />
    public Task<int?> DetectFromWktAsync(string wktContent, CancellationToken cancellationToken = default)
        => Task.FromResult<int?>(null);

    /// <inheritdoc />
    public int? DetectFromEpsgCode(string epsgCode) => null;

    /// <inheritdoc />
    public Task<int?> DetectFromGeoJsonCrsAsync(string crsObject, CancellationToken cancellationToken = default)
        => Task.FromResult<int?>(null);

    /// <inheritdoc />
    public Task<int?> DetectFromShapefilePrjAsync(string shapefilePath, CancellationToken cancellationToken = default)
        => Task.FromResult<int?>(null);

    /// <inheritdoc />
    public Task<bool> ValidateSridAsync(int srid, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
