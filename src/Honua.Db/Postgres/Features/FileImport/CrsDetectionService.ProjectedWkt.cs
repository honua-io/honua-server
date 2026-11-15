// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class CrsDetectionService
{
    private async Task<int?> FindSridByProjectedParametersAsync(string wkt, CancellationToken cancellationToken)
    {
        var definition = ProjectedWktDefinition.Parse(wkt);
        if (definition == null) return null;
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand("""
                SELECT srid, srtext FROM spatial_ref_sys
                WHERE auth_name = 'EPSG' AND srtext LIKE 'PROJCS[%'
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            int? match = null;
            while (await reader.ReadAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = ProjectedWktDefinition.Parse(reader.GetString(1));
                if (candidate == null || !definition.Matches(candidate)) continue;
                // More than one registered EPSG definition is ambiguous; require an explicit override.
                if (match.HasValue) return null;
                match = reader.GetInt32(0);
            }
            return match;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            CrsLog.SridDetectionByWktFailed(_logger, exception);
            return null;
        }
    }
}
