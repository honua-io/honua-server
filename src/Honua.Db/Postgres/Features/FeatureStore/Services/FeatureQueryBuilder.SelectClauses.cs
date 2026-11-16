// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    internal static bool ShouldComputeDistance(SpatialFilter? spatialFilter)
        => spatialFilter is { ReturnDistance: true };

    private string BuildDistanceSelectOperand(
        SpatialFilter filter,
        FeatureQuery query,
        bool isKnnQuery,
        ref int paramIndex,
        List<object> parameters)
    {
        var expression = BuildGeographyFilterExpression(filter, query, ref paramIndex);
        if (!isKnnQuery)
        {
            parameters.Add(filter.Geometry);
        }

        return expression;
    }

    private void BuildSelectClause(
        StringBuilder sql,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        bool isKnnQuery,
        SpatialFilter? spatialFilter,
        ref int paramIndex,
        List<object> parameters)
    {
        var geometrySelect = _geometryProcessor.GetGeometrySelectExpression(geometryStorageType, query);
        var attributesSelect = BuildAttributesSelectExpression(query, ref paramIndex, parameters);

        if (ShouldComputeDistance(spatialFilter))
        {
            // The runtime distance column requires KNN/spatial parameter ordering the overlay does not yet
            // thread; versioned distance reads are unsupported in v1. DEFAULT is unaffected.
            GuardVersionedReadSupported(query, "select-with-distance");
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildDistanceSelectOperand(spatialFilter!.Value, query, isKnnQuery, ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect}, {attributesSelect} AS {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as {FeatureQueryEncoding.InternalDistanceColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            var featureSource = BuildVersionedFeatureSource(query, "features", ref paramIndex, parameters);
            if (query.Distinct)
            {
                // Distinct values are attribute rows, not features: including objectid or
                // geometry in the DISTINCT projection would make every row unique. Keep the
                // reader's stable three-column shape while returning a null synthetic id and
                // geometry for the protocol formatter to suppress.
                sql.Append(CultureInfo.InvariantCulture,
                    $"SELECT DISTINCT 0::bigint AS {DatabaseSchema.ObjectIdColumn}, NULL AS {DatabaseSchema.GeometryColumn}, {attributesSelect} AS {DatabaseSchema.AttributesColumn} FROM {featureSource} WHERE {DatabaseSchema.LayerIdColumn} = $1");
            }
            else
            {
                sql.Append(CultureInfo.InvariantCulture,
                    $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect}, {attributesSelect} AS {DatabaseSchema.AttributesColumn} FROM {featureSource} WHERE {DatabaseSchema.LayerIdColumn} = $1");
            }
        }
    }

    private void BuildGmlSelectClause(
        StringBuilder sql,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        bool isKnnQuery,
        SpatialFilter? spatialFilter,
        ref int paramIndex,
        List<object> parameters)
    {
        var geometrySelect = _geometryProcessor.GetGeometryGmlExpression(geometryStorageType, query);
        var attributesSelect = BuildAttributesSelectExpression(query, ref paramIndex, parameters);

        if (ShouldComputeDistance(spatialFilter))
        {
            GuardVersionedReadSupported(query, "select-gml-with-distance");
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildDistanceSelectOperand(spatialFilter!.Value, query, isKnnQuery, ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS {FeatureQueryEncoding.GeometryColumn}, {attributesSelect} AS {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as {FeatureQueryEncoding.InternalDistanceColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            var featureSource = BuildVersionedFeatureSource(query, "features", ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS geometry, {attributesSelect} AS {DatabaseSchema.AttributesColumn} FROM {featureSource} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
    }

    private void BuildGeoJsonSelectClause(
        StringBuilder sql,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        bool isKnnQuery,
        SpatialFilter? spatialFilter,
        ref int paramIndex,
        List<object> parameters)
    {
        var geometrySelect = _geometryProcessor.GetGeometryGeoJsonExpression(geometryStorageType, query);
        var attributesSelect = BuildAttributesSelectExpression(query, ref paramIndex, parameters);

        if (ShouldComputeDistance(spatialFilter))
        {
            GuardVersionedReadSupported(query, "select-geojson-with-distance");
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildDistanceSelectOperand(spatialFilter!.Value, query, isKnnQuery, ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS {FeatureQueryEncoding.GeometryColumn}, {attributesSelect} AS {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as {FeatureQueryEncoding.InternalDistanceColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            var featureSource = BuildVersionedFeatureSource(query, "features", ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS geometry, {attributesSelect} AS {DatabaseSchema.AttributesColumn} FROM {featureSource} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
    }

    private void BuildRawGeoJsonSelectClause(
        StringBuilder sql,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        bool isKnnQuery,
        SpatialFilter? spatialFilter,
        ref int paramIndex,
        List<object> parameters)
    {
        var geometrySelect = _geometryProcessor.GetGeometryGeoJsonExpression(geometryStorageType, query);
        var (publicIdSelect, attributesSelect) = BuildRawAttributeSelectExpressions(query, ref paramIndex, parameters);

        if (ShouldComputeDistance(spatialFilter))
        {
            GuardVersionedReadSupported(query, "select-raw-geojson-with-distance");
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildDistanceSelectOperand(spatialFilter!.Value, query, isKnnQuery, ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS {FeatureQueryEncoding.GeometryColumn}, {publicIdSelect} AS public_id, {attributesSelect} AS {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as {FeatureQueryEncoding.InternalDistanceColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            var featureSource = BuildVersionedFeatureSource(query, "features", ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS geometry, {publicIdSelect} AS public_id, {attributesSelect} AS {DatabaseSchema.AttributesColumn} FROM {featureSource} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
    }

    private void BuildKmlSelectClause(
        StringBuilder sql,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        bool isKnnQuery,
        SpatialFilter? spatialFilter,
        ref int paramIndex,
        List<object> parameters)
    {
        var geometrySelect = _geometryProcessor.GetGeometryKmlExpression(geometryStorageType, query);
        var attributesSelect = BuildAttributesSelectExpression(query, ref paramIndex, parameters);

        if (ShouldComputeDistance(spatialFilter))
        {
            GuardVersionedReadSupported(query, "select-kml-with-distance");
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var distanceParamExpression = BuildDistanceSelectOperand(spatialFilter!.Value, query, isKnnQuery, ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS {FeatureQueryEncoding.GeometryColumn}, {attributesSelect} AS {DatabaseSchema.AttributesColumn}, ST_Distance({geographyOperand}, {distanceParamExpression}) as {FeatureQueryEncoding.InternalDistanceColumn} FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
        else
        {
            var featureSource = BuildVersionedFeatureSource(query, "features", ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {DatabaseSchema.ObjectIdColumn}, {geometrySelect} AS geometry, {attributesSelect} AS {DatabaseSchema.AttributesColumn} FROM {featureSource} WHERE {DatabaseSchema.LayerIdColumn} = $1");
        }
    }

    private static string BuildAttributesSelectExpression(
        FeatureQuery query,
        ref int paramIndex,
        List<object> parameters)
    {
        if (query.ExcludeAttributes)
        {
            return "NULL";
        }

        // Field-level security: the enforced masked-field set is the source of
        // attribute columns this expression may project, regardless of the requested
        // outFields. Resolved provider-side, so a restricted role never receives a masked
        // column even when it explicitly asks for it. Empty/unset means no masking.
        var maskedFields = ResolveMaskedFields(query);

        if (!query.OutFields.HasValue || query.OutFields.Value.IsDefault)
        {
            // All fields: subtract masked columns from the attributes jsonb via the
            // key-removal operator (field names bound as a parameter, injection-safe).
            return BuildMaskedAttributesColumn(maskedFields, ref paramIndex, parameters);
        }

        var requestedOutFields = query.OutFields.Value;
        if (requestedOutFields.IsEmpty)
        {
            return "NULL";
        }

        var projectedFields = new List<string>(requestedOutFields.Length);
        var seenFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fieldName in requestedOutFields)
        {
            // Projected names are jsonb keys, not SQL identifiers: both the key and the
            // value accessor bind the name as a parameter below, so this predicate is a
            // shape guard rather than the thing that makes the SQL safe. See
            // FeatureQueryBuilder.IsValidJsonAttributeKey for why it is deliberately
            // separate from the identifier-oriented IsValidFieldName.
            if (!IsValidJsonAttributeKey(fieldName))
            {
                throw new ArgumentException($"Invalid field name for projection: {fieldName}");
            }

            // A masked field is dropped even when explicitly requested (fail-secure).
            if (maskedFields.Contains(fieldName))
            {
                continue;
            }

            var isDistinctObjectId = query.Distinct &&
                (fieldName.Equals(DatabaseSchema.ObjectIdColumn, StringComparison.OrdinalIgnoreCase)
                 || fieldName.Equals(DatabaseSchema.ObjectIdColumnAlt, StringComparison.OrdinalIgnoreCase));
            if ((!DatabaseSchema.CanUseJsonPath(fieldName) && !isDistinctObjectId) || !seenFields.Add(fieldName))
            {
                continue;
            }

            // Bind the key side as a parameter too, not just the value side. Postgres
            // accepts a bound parameter as a jsonb_build_object key, so no part of a
            // field name is ever interpolated into the SQL text and the projection is
            // injection-safe independently of the character class the validator above
            // admits. The same positional parameter serves both halves of the pair; the
            // explicit ::text casts keep the overloaded `jsonb -> ?` operator (which also
            // has an integer-subscript form) unambiguous.
            var fieldParamIndex = paramIndex++;
            parameters.Add(fieldName);
            var valueExpression = isDistinctObjectId
                ? DatabaseSchema.ObjectIdColumn
                : $"{DatabaseSchema.AttributesColumn} -> ${fieldParamIndex}::text";
            projectedFields.Add($"${fieldParamIndex}::text, {valueExpression}");
        }

        if (projectedFields.Count == 0)
        {
            return "NULL";
        }

        return $"jsonb_build_object({string.Join(", ", projectedFields)})::text";
    }

    private static HashSet<string> ResolveMaskedFields(FeatureQuery query)
    {
        var masked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (query.EnforcedMaskedFields is { IsDefaultOrEmpty: false } fields)
        {
            masked.UnionWith(fields
                .Where(field => !string.IsNullOrWhiteSpace(field))
                .Select(field => field.Trim()));
        }

        return masked;
    }

    internal static string BuildMaskedAttributesColumn(
        HashSet<string> maskedFields,
        ref int paramIndex,
        List<object> parameters)
    {
        if (maskedFields.Count == 0)
        {
            return DatabaseSchema.AttributesColumn;
        }

        var maskParamIndex = paramIndex++;
        parameters.Add(maskedFields.Select(static field => field.ToUpperInvariant()).ToArray());
        // JSON object keys are case-sensitive while policy field matching is not. Rebuild
        // the object with a case-insensitive comparison so a differently-cased policy can
        // never fail open on wildcard/default projections.
        return $"(SELECT COALESCE(jsonb_object_agg(masked_attr.key, masked_attr.value), '{{}}'::jsonb) FROM jsonb_each({DatabaseSchema.AttributesColumn}) AS masked_attr(key, value) WHERE NOT (UPPER(masked_attr.key) = ANY(${maskParamIndex}::text[])))";
    }

    private static (string PublicIdSelect, string AttributesSelect) BuildRawAttributeSelectExpressions(
        FeatureQuery query,
        ref int paramIndex,
        List<object> parameters)
    {
        if (query.ExcludeAttributes)
        {
            return ("NULL", "NULL");
        }

        // Field-level security: subtract masked columns from the raw attributes
        // jsonb (in addition to the promoted public-id field), so the GeoJSON/GeoServices
        // raw fast paths honor masking just like the materialized-Feature paths.
        var maskedFields = ResolveMaskedFields(query);

        // The public-id attribute name is bound as a parameter on both sides of this
        // expression (the `-> $n` promotion and the `- $n` key removal), so it is a
        // jsonb key, not an identifier — gating it on IsValidFieldName silently
        // un-promoted a prefixed public-id field instead of projecting it.
        if (!string.IsNullOrWhiteSpace(query.PublicIdAttributeName) &&
            IsValidJsonAttributeKey(query.PublicIdAttributeName))
        {
            var fieldParamIndex = paramIndex++;
            parameters.Add(query.PublicIdAttributeName);
            var fieldParam = $"${fieldParamIndex}";
            var attributesExpr = ApplyMaskRemoval(
                $"({DatabaseSchema.AttributesColumn} - {fieldParam})", maskedFields, ref paramIndex, parameters);
            return (
                $"{DatabaseSchema.AttributesColumn} -> {fieldParam}",
                $"{attributesExpr}::text");
        }

        var unprojected = ApplyMaskRemoval(
            DatabaseSchema.AttributesColumn, maskedFields, ref paramIndex, parameters);
        return ("NULL", $"{unprojected}::text");
    }

    private static string ApplyMaskRemoval(
        string attributesExpression,
        HashSet<string> maskedFields,
        ref int paramIndex,
        List<object> parameters)
    {
        if (maskedFields.Count == 0)
        {
            return attributesExpression;
        }

        var maskParamIndex = paramIndex++;
        parameters.Add(maskedFields.ToArray());
        return $"({attributesExpression} - ${maskParamIndex})";
    }
}
