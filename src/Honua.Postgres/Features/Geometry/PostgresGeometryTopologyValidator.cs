// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;

namespace Honua.Postgres.Features.Geometry;

internal sealed class PostgresGeometryTopologyValidator(
    IDatabaseConnectionProvider connectionProvider) : IGeometryTopologyValidator
{
    private readonly IDatabaseConnectionProvider _connectionProvider = connectionProvider
        ?? throw new ArgumentNullException(nameof(connectionProvider));

    public async Task<GeometryValidationResult> ValidateTopologyAsync(
        byte[] wkb,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ST_IsValid($1::geometry), ST_IsValidReason($1::geometry)";
        cmd.Parameters.Add(new NpgsqlParameter { Value = wkb });

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var isValid = reader.GetBoolean(0);
            var reason = reader.IsDBNull(1) ? null : reader.GetString(1);

            if (isValid)
            {
                return GeometryValidationResult.Success();
            }

            var errorCode = MapPostGisReasonToErrorCode(reason);
            return GeometryValidationResult.Failure(errorCode, reason ?? "Geometry is topologically invalid");
        }

        return GeometryValidationResult.Success();
    }

    public async Task<GeometryRepairResult> RepairAsync(
        byte[] wkb,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        string? originalReason;
        await using (var reasonCmd = connection.CreateCommand())
        {
            reasonCmd.CommandText = "SELECT ST_IsValidReason($1::geometry)";
            reasonCmd.Parameters.Add(new NpgsqlParameter { Value = wkb });
            var result = await reasonCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            originalReason = result as string;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                ST_AsBinary(repaired),
                ST_GeometryType(repaired),
                ST_GeometryType($1::geometry) as original_type,
                ST_IsEmpty(repaired)
            FROM (SELECT ST_MakeValid($1::geometry) as repaired) sub
            """;
        cmd.Parameters.Add(new NpgsqlParameter { Value = wkb });

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var isEmpty = reader.GetBoolean(3);
            if (isEmpty)
            {
                return GeometryRepairResult.Failed(
                    "Repair resulted in empty geometry",
                    originalReason);
            }

            var repairedWkb = reader.IsDBNull(0) ? null : (byte[])reader[0];
            if (repairedWkb == null)
            {
                return GeometryRepairResult.Failed("Repair produced null result", originalReason);
            }

            var repairedType = reader.GetString(1);
            var originalType = reader.GetString(2);
            var typeChanged = !string.Equals(repairedType, originalType, StringComparison.OrdinalIgnoreCase);

            var repairs = new List<string>();
            if (originalReason != null && !originalReason.StartsWith("Valid Geometry", StringComparison.OrdinalIgnoreCase))
            {
                repairs.Add($"Fixed: {originalReason}");
            }

            if (typeChanged)
            {
                repairs.Add($"Geometry type changed from {originalType} to {repairedType}");
            }

            return new GeometryRepairResult
            {
                Success = true,
                RepairedWkb = repairedWkb,
                RepairsApplied = repairs.ToImmutableList(),
                OriginalValidationReason = originalReason,
                GeometryTypeChanged = typeChanged
            };
        }

        return GeometryRepairResult.Failed("No result from repair operation", originalReason);
    }

    private static ValidationErrorCode MapPostGisReasonToErrorCode(string? reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return ValidationErrorCode.InvalidTopology;
        }

        return reason.ToUpperInvariant() switch
        {
            var r when r.Contains("SELF-INTERSECTION") => ValidationErrorCode.SelfIntersection,
            var r when r.Contains("DUPLICATE") => ValidationErrorCode.DuplicatePoints,
            var r when r.Contains("RING") && r.Contains("ORIENT") => ValidationErrorCode.IncorrectOrientation,
            var r when r.Contains("HOLE") && r.Contains("OUTSIDE") => ValidationErrorCode.HoleOutsideShell,
            var r when r.Contains("NESTED") => ValidationErrorCode.NestedHoles,
            var r when r.Contains("DISCONNECT") => ValidationErrorCode.DisconnectedInterior,
            _ => ValidationErrorCode.InvalidTopology
        };
    }
}
