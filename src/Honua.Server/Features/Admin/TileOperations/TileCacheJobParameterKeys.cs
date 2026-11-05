// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.TileOperations;

internal static class TileCacheJobParameterKeys
{
    public const string Operation = "honua.tilecache.operation";

    public const string ServiceId = "honua.tilecache.service_id";

    public const string LayerId = "honua.tilecache.layer_id";

    public const string TileMatrixSetId = "honua.tilecache.tile_matrix_set_id";

    public const string MinZoom = "honua.tilecache.min_zoom";

    public const string MaxZoom = "honua.tilecache.max_zoom";

    public const string Bbox = "honua.tilecache.bbox";

    public const string MaxTiles = "honua.tilecache.max_tiles";

    public const string SchemaName = "honua.tilecache.schema_name";

    public const string TenantScope = "honua.tilecache.tenant_scope";

    public const string Compression = "honua.tilecache.compression";

    public const string Style = "honua.tilecache.style";

    public const string Format = "honua.tilecache.format";

    // When no generation id is supplied the worker uses the operation id, which retries preserve, so a retry resumes the same checkpoint.
    public const string GenerationId = "honua.tilecache.generation_id";
}
