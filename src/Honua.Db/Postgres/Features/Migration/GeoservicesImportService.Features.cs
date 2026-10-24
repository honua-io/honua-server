// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Shared.Models;
using Npgsql;
using NpgsqlTypes;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Db.Postgres.Features.Migration;
using Honua.Db.Postgres.Features.FileImport;

namespace Honua.Db.Postgres.Features.Migration;

internal sealed partial class GeoservicesImportService
{
    private async Task<InsertFeaturesResult> InsertFeaturesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schemaName,
        string tableName,
        GeoservicesLayerInfo layerInfo,
        ArcGisFeature[] features,
        int targetSrid,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inserted = 0;
        var failed = 0;
        string? firstError = null;
        string? firstFailureReason = null;
        var higherDimensionCount = 0;

        // Build insert statement. Column names follow the same mapping as CREATE TABLE so a
        // source OBJECTID/GEOM attribute, or two names that sanitize identically, cannot collide
        // with the surrogate key or with each other. Attribute values are still read by source name.
        var columnMap = GeoservicesImportColumns.Map(layerInfo);
        var fields = columnMap.Columns.Select(column => column.Field).ToArray();
        var hasGeometry = !string.IsNullOrEmpty(layerInfo.GeometryType);
        var sourceObjectIdField = layerInfo.Fields.FirstOrDefault(f => f.IsObjectId)?.Name;
        var objectIdMap = new Dictionary<long, long>(features.Length);
        var absentGeometryTargetIds = new HashSet<long>();
        var unconvertedGeometryTargetIds = new HashSet<long>();
        var invalidSourceGeometryTargetIds = new HashSet<long>();

        var columnList = new List<string>(fields.Length + 1);
        columnList.AddRange(columnMap.Columns.Select(column => $"\"{column.ColumnName}\""));
        if (hasGeometry)
        {
            columnList.Add("geom");
        }

        var columnNames = string.Join(", ", columnList);

        var parameterPlaceholders = string.Join(", ", fields.Select((_, i) => $"@p{i}"));
        if (hasGeometry)
        {
            // A geometry-only layer has no attribute placeholders. Appending with a comma
            // would emit VALUES (, geom-expr), which PostgreSQL rejects.
            var geometryExpression = BuildGeometryInsertExpression(layerInfo.GeometryType, targetSrid);
            parameterPlaceholders = parameterPlaceholders.Length == 0
                ? geometryExpression
                : $"{parameterPlaceholders}, {geometryExpression}";
        }

        // Classify the source WKT, not the geometry stored after ST_MakeValid. A repaired
        // polygon remains an inherited source defect or an unrepaired rewrite (#4826).
        var sourceValidity = hasGeometry
            ? $", CASE WHEN @geom IS NULL THEN NULL ELSE ST_IsValid(ST_GeomFromText(@geom, {targetSrid})) END"
            : string.Empty;
        var insertSql =
            $"INSERT INTO {QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)} ({columnNames}) VALUES ({parameterPlaceholders}) RETURNING {FieldNames.ObjectId}{sourceValidity}";

        // Create the command once, add parameters with placeholder values, and prepare
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = insertSql;
        cmd.Transaction = transaction;

        for (var i = 0; i < fields.Length; i++)
        {
            cmd.Parameters.AddWithValue($"p{i}", DBNull.Value);
        }

        if (hasGeometry)
        {
            cmd.Parameters.Add("geom", NpgsqlDbType.Text).Value = DBNull.Value;
        }

        await cmd.PrepareAsync(cancellationToken);

        // Each feature insert is wrapped in a savepoint so a single malformed row does not
        // abort the outer transaction (PostgreSQL error code 25P02 "current transaction is
        // aborted" poisons every subsequent command in the same transaction once any
        // statement fails). The savepoint rolls back the aborted sub-transaction, leaving
        // the outer transaction intact for the next feature and the final commit.
        var savepointName = $"sp_feat_{tableName.GetHashCode():x8}";

        foreach (var feature in features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A failed SAVEPOINT has no row scope to roll back to.
            await transaction.SaveAsync(savepointName, cancellationToken).ConfigureAwait(false);
            try
            {
                // Update attribute parameter values
                for (var i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    object? value = null;

                    if (feature.Attributes?.TryGetValue(field.Name, out var jsonValue) == true)
                    {
                        value = ConvertJsonValue(jsonValue, field.Type);
                    }

                    cmd.Parameters[$"p{i}"].Value = value ?? DBNull.Value;
                }

                // Update geometry parameter value. A present source geometry that cannot be
                // converted is still inserted as NULL so one bad shape does not drop the row,
                // and the target id is recorded as transfer loss rather than an inherited null.
                var sourceGeometryAbsent = false;
                var sourceGeometryUnconverted = false;
                if (hasGeometry && feature.Geometry.HasValue)
                {
                    if (HasHigherDimensionCoordinates(feature.Geometry.Value))
                    {
                        higherDimensionCount++;
                    }

                    var wkt = ConvertEsriGeometryToWkt(feature.Geometry.Value, layerInfo.HasZ, layerInfo.HasM);
                    if (wkt is null)
                    {
                        sourceGeometryUnconverted = true;
                        Log.GeometryConversionFailed(_logger, tableName);
                    }

                    cmd.Parameters["geom"].Value = wkt ?? (object)DBNull.Value;
                }
                else if (hasGeometry)
                {
                    sourceGeometryAbsent = true;
                    cmd.Parameters["geom"].Value = DBNull.Value;
                }

                long? insertedId = null;
                bool? sourceGeometryValid = null;
                await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !reader.IsDBNull(0))
                    {
                        insertedId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                        if (hasGeometry && !reader.IsDBNull(1))
                        {
                            sourceGeometryValid = reader.GetBoolean(1);
                        }
                    }
                }

                inserted++;

                if (insertedId is { } targetFeatureId)
                {
                    if (sourceGeometryAbsent)
                    {
                        absentGeometryTargetIds.Add(targetFeatureId);
                    }
                    else if (sourceGeometryUnconverted)
                    {
                        unconvertedGeometryTargetIds.Add(targetFeatureId);
                    }
                    else if (sourceGeometryValid == false)
                    {
                        invalidSourceGeometryTargetIds.Add(targetFeatureId);
                    }

                    if (sourceObjectIdField is not null
                        && TryReadSourceObjectId(feature, sourceObjectIdField, out var sourceOid))
                    {
                        objectIdMap[sourceOid] = targetFeatureId;
                    }
                }
            }
            // Intentionally broad: per-feature insert failure; roll back to the savepoint so the
            // outer transaction is not poisoned by the 25P02 aborted-transaction state. Use
            // CancellationToken.None: if the caller's token has been cancelled we still need to
            // clean up the transaction state.
            catch (Exception ex) when (ex is not OutOfMemoryException
                && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                await transaction.RollbackAsync(savepointName, CancellationToken.None).ConfigureAwait(false);
                firstError ??= "Feature import failed.";
                firstFailureReason ??= DescribeFeatureInsertFailure(ex);
                Log.FeatureInsertFailed(_logger, ex.Message);
                failed++;
            }

            // Reusing a name only shadows an older savepoint. RELEASE is required
            // after both success and ROLLBACK TO so subtransaction locks stay bounded.
            // Finish this cleanup even if cancellation arrived after the row insert;
            // the caller still owns rollback of the outer import transaction.
            await transaction.ReleaseAsync(savepointName, CancellationToken.None).ConfigureAwait(false);
        }

        if (firstError is not null)
        {
            Log.FeatureInsertFailures(_logger, failed, firstError);
        }

        if (higherDimensionCount > 0)
        {
            Log.HigherDimensionGeometryDetected(_logger, higherDimensionCount, tableName);
        }

        return new InsertFeaturesResult(
            inserted,
            failed,
            objectIdMap,
            firstFailureReason,
            absentGeometryTargetIds,
            unconvertedGeometryTargetIds,
            invalidSourceGeometryTargetIds);
    }

    private static bool TryReadSourceObjectId(
        ArcGisFeature feature,
        string sourceObjectIdField,
        out long sourceObjectId)
    {
        sourceObjectId = 0;
        if (feature.Attributes is null)
        {
            return false;
        }

        if (!feature.Attributes.TryGetValue(sourceObjectIdField, out var element))
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var int64))
                {
                    sourceObjectId = int64;
                    return true;
                }

                if (element.TryGetDouble(out var dbl) && !double.IsNaN(dbl) && !double.IsInfinity(dbl))
                {
                    sourceObjectId = (long)dbl;
                    return true;
                }

                return false;

            case JsonValueKind.String:
                return long.TryParse(
                    element.GetString(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out sourceObjectId);

            default:
                return false;
        }
    }

    /// <summary>
    /// Outcome of a single InsertFeaturesAsync batch: insert/fail counters plus a
    /// mapping from source ObjectId to the BIGSERIAL identifier assigned by the
    /// imported table. Used to link attachments back to the persisted features.
    /// <see cref="FirstFailureReason"/> is the redacted reason the batch's first failed row was
    /// rejected (see <see cref="DescribeFeatureInsertFailure"/>), or <see langword="null"/> when no
    /// row failed.
    /// </summary>
    internal readonly record struct InsertFeaturesResult(
        int Inserted,
        int Failed,
        Dictionary<long, long> ObjectIdMap,
        string? FirstFailureReason = null,
        HashSet<long>? AbsentGeometryTargetIds = null,
        HashSet<long>? UnconvertedGeometryTargetIds = null,
        HashSet<long>? InvalidSourceGeometryTargetIds = null);

    private static string BuildGeometryInsertExpression(string? geometryType, int targetSrid)
    {
        var geometry = $"ST_GeomFromText(@geom, {targetSrid})";
        return geometryType?.ToUpperInvariant() switch
        {
            "ESRIGEOMETRYPOLYGON" => $"ST_Multi(ST_CollectionExtract(ST_MakeValid({geometry}), 3))",
            "ESRIGEOMETRYPOLYLINE" => $"ST_Multi(ST_CollectionExtract(ST_MakeValid({geometry}), 2))",
            _ => geometry
        };
    }

    private static object? ConvertJsonValue(JsonElement element, string esriType)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return null;

        return esriType.ToUpperInvariant() switch
        {
            "ESRIFIELDTYPEOID" or "ESRIFIELDTYPEBIGINTEGER" =>
                element.ValueKind == JsonValueKind.Number ? element.GetInt64() : null,

            "ESRIFIELDTYPEINTEGER" or "ESRIFIELDTYPESMALLINTEGER" =>
                element.ValueKind == JsonValueKind.Number ? element.GetInt32() : null,

            "ESRIFIELDTYPEDOUBLE" or "ESRIFIELDTYPESINGLE" =>
                element.ValueKind == JsonValueKind.Number ? element.GetDouble() : null,

            "ESRIFIELDTYPESTRING" =>
                element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString(),

            "ESRIFIELDTYPEDATE" =>
                element.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeMilliseconds(element.GetInt64())
                    : null,

            "ESRIFIELDTYPEDATEONLY" =>
                element.ValueKind == JsonValueKind.String
                    && DateOnly.TryParseExact(element.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var dateOnly)
                    ? dateOnly
                    : throw new FormatException("Date-only fields require an ISO calendar date (yyyy-MM-dd)."),

            "ESRIFIELDTYPEGUID" or "ESRIFIELDTYPEGLOBALID" =>
                element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var guid)
                    ? guid
                    : null,

            "ESRIFIELDTYPEBLOB" or "ESRIFIELDTYPERASTER" =>
                element.ValueKind == JsonValueKind.String
                    ? DecodeBinaryValue(element.GetString())
                    : null,

            _ => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()
        };
    }

    private static byte[]? DecodeBinaryValue(string? value)
    {
        if (value is null)
            return null;

        return Convert.FromBase64String(value);
    }
}
