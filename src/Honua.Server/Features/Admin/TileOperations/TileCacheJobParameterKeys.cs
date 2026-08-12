// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.TileOperations;

/// <summary>
/// Stable <see cref="Honua.Core.Features.ControlPlane.Domain.ExecutionJobSpec.Parameters"/>
/// keys used to encode a tile-cache / PMTiles request onto a durable
/// <see cref="Honua.Core.Features.ControlPlane.Domain.ExecutionJobKind.TileCache"/>
/// execution job. The worker-side <see cref="TileCacheJobExecutor"/> reads these
/// keys back so it can run the requested operation without reaching into the admin
/// request cache. The set is intentionally small and self-describing so the same
/// spec is portable across the local in-process worker and a remote GDAL/tippecanoe
/// batch container.
/// </summary>
internal static class TileCacheJobParameterKeys
{
    /// <summary>Tile operation verb (seed/warm/invalidate/purge/archive/publish).</summary>
    public const string Operation = "honua.tilecache.operation";

    /// <summary>Service identifier the operation targets, when scoped to a service.</summary>
    public const string ServiceId = "honua.tilecache.service_id";

    /// <summary>Single layer identifier the operation targets, when scoped to a layer.</summary>
    public const string LayerId = "honua.tilecache.layer_id";

    /// <summary>Tile matrix set / gridset identifier (for example <c>WebMercatorQuad</c>).</summary>
    public const string TileMatrixSetId = "honua.tilecache.tile_matrix_set_id";

    /// <summary>Minimum zoom level of the requested range.</summary>
    public const string MinZoom = "honua.tilecache.min_zoom";

    /// <summary>Maximum zoom level of the requested range.</summary>
    public const string MaxZoom = "honua.tilecache.max_zoom";

    /// <summary>
    /// Bounding box as a comma-separated <c>minLon,minLat,maxLon,maxLat</c> string.
    /// Absent when the request did not constrain the bbox.
    /// </summary>
    public const string Bbox = "honua.tilecache.bbox";

    /// <summary>Maximum tile count requested by the caller, when supplied.</summary>
    public const string MaxTiles = "honua.tilecache.max_tiles";

    /// <summary>
    /// Schema name (multi-tenant routing) captured at submission so the worker
    /// resolves metadata and tiles against the same schema the caller used.
    /// </summary>
    public const string SchemaName = "honua.tilecache.schema_name";

    /// <summary>
    /// Output compression for archive/publish artifacts. Reserved for forward
    /// compatibility with tippecanoe-style workers; the managed writer currently
    /// emits uncompressed tiles.
    /// </summary>
    public const string Compression = "honua.tilecache.compression";

    /// <summary>Style identifier the cached tiles were rendered with (bounded expire/delete window, #2661).</summary>
    public const string Style = "honua.tilecache.style";

    /// <summary>Output format the cached tiles were written as (bounded expire/delete window, #2661).</summary>
    public const string Format = "honua.tilecache.format";

    /// <summary>
    /// Stable generation identifier for a resumable seed/warm run (issue #2661). Optional; when
    /// absent the worker uses the execution job's operation id (which durable retries preserve) so
    /// a retry resumes the same generation checkpoint instead of restarting the grid.
    /// </summary>
    public const string GenerationId = "honua.tilecache.generation_id";
}
