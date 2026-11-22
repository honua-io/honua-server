// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Raster;

// A request the import refuses before touching the raster. Detail is fixed text written at the
// throw site, so it is returned to the caller; every other import failure stays "Import failed.".
internal sealed class RasterImportRequestException(string detail) : ArgumentException(detail)
{
    public string Detail { get; } = detail;
}
