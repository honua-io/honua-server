// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;

namespace Honua.Db.Postgres.Features.Raster;

internal sealed class PostgresRasterStorageAvailability(IAdoNetDatabaseConnectionProvider connectionProvider)
    : IRasterStorageAvailability
{
    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await PostgresRasterStorageProbe.IsInstalledAsync(connection, cancellationToken).ConfigureAwait(false);
    }
}
