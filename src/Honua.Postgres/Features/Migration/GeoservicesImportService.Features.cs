// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.Migration;

internal sealed partial class GeoservicesImportService
{
    private async Task<InsertFeaturesResult> InsertFeaturesAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        GeoservicesLayerInfo layerInfo,
        ArcGisFeature[] features,
        int targetSrid,
        CancellationToken cancellationToken)
    {
        var inserted = 0;
        var failed = 0;
        string? firstError = null;
        var higherDimensionCount = 0;

        // Build insert statement
        var fields = layerInfo.Fields.Where(f => !f.IsObjectId && !IsGeometryField(f)).ToArray();
        var hasGeometry = !string.IsNullOrEmpty(layerInfo.GeometryType);
        var sourceObjectIdField = layerInfo.Fields.FirstOrDefault(f => f.IsObjectId)?.Name;
        var objectIdMap = new Dictionary<long, long>(features.Length);

        var columnNames = string.Join(", ", fields.Select(f => $"\"{f.Name.SanitizeFieldName()}\""));
        if (hasGeometry)
        {
            columnNames += ", geom";
        }

        var parameterPlaceholders = string.Join(", ", fields.Select((_, i) => $"@p{i}"));
        if (hasGeometry)
        {
            parameterPlaceholders += $", {BuildGeometryInsertExpression(layerInfo.GeometryType, targetSrid)}";
        }

        var insertSql =
            $"INSERT INTO {QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)} ({columnNames}) VALUES ({parameterPlaceholders}) RETURNING {FieldNames.ObjectId}";

        // Create the command once, add parameters with placeholder values, and prepare
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = insertSql;

        for (var i = 0; i < fields.Length; i++)
        {
            cmd.Parameters.AddWithValue($"p{i}", DBNull.Value);
        }

        if (hasGeometry)
        {
            cmd.Parameters.Add("geom", NpgsqlDbType.Text).Value = DBNull.Value;
        }

        await cmd.PrepareAsync(cancellationToken);

        foreach (var feature in features)
        {
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

                // Update geometry parameter value
                if (hasGeometry && feature.Geometry.HasValue)
                {
                    if (HasHigherDimensionCoordinates(feature.Geometry.Value))
                    {
                        higherDimensionCount++;
                    }

                    var geoJson = ConvertEsriGeometryToGeoJson(feature.Geometry.Value);
                    if (geoJson is null)
                    {
                        Log.GeometryConversionFailed(_logger, tableName);
                    }

                    cmd.Parameters["geom"].Value = geoJson ?? (object)DBNull.Value;
                }
                else if (hasGeometry)
                {
                    cmd.Parameters["geom"].Value = DBNull.Value;
                }

                var rawInsertedId = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                inserted++;

                if (sourceObjectIdField is not null
                    && TryReadSourceObjectId(feature, sourceObjectIdField, out var sourceOid)
                    && rawInsertedId is not null
                    && rawInsertedId is not DBNull)
                {
                    objectIdMap[sourceOid] = Convert.ToInt64(rawInsertedId, System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
                Log.FeatureInsertFailed(_logger, ex.Message);
                failed++;
            }
        }

        if (firstError is not null)
        {
            Log.FeatureInsertFailures(_logger, failed, firstError);
        }

        if (higherDimensionCount > 0)
        {
            Log.HigherDimensionGeometryDetected(_logger, higherDimensionCount, tableName);
        }

        return new InsertFeaturesResult(inserted, failed, objectIdMap);
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
    /// </summary>
    internal readonly record struct InsertFeaturesResult(
        int Inserted,
        int Failed,
        Dictionary<long, long> ObjectIdMap);

    private static string BuildGeometryInsertExpression(string? geometryType, int targetSrid)
    {
        var geometry = $"ST_SetSRID(ST_GeomFromGeoJSON(@geom), {targetSrid})";
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
            "ESRIFIELDTYPEOID" or "ESRIFIELDTYPEINTEGER" or "ESRIFIELDTYPESMALLINTEGER" =>
                element.ValueKind == JsonValueKind.Number ? element.GetInt32() : null,

            "ESRIFIELDTYPEDOUBLE" or "ESRIFIELDTYPESINGLE" =>
                element.ValueKind == JsonValueKind.Number ? element.GetDouble() : null,

            "ESRIFIELDTYPESTRING" =>
                element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString(),

            "ESRIFIELDTYPEDATE" =>
                element.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeMilliseconds(element.GetInt64())
                    : null,

            "ESRIFIELDTYPEGUID" or "ESRIFIELDTYPEGLOBALID" =>
                element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var guid)
                    ? guid
                    : null,

            _ => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()
        };
    }
}
