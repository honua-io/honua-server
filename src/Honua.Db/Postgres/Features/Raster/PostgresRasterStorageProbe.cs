// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;

namespace Honua.Db.Postgres.Features.Raster;

// The raster migration root runs only when postgis_raster is installed, so a feature-only
// install has no raster tables. Raster reads treat that as "no rasters"; raster writes refuse.
internal static class PostgresRasterStorageProbe
{
    public static async Task<bool> IsInstalledAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_extension WHERE extname = 'postgis_raster')";
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not false;
    }
}
