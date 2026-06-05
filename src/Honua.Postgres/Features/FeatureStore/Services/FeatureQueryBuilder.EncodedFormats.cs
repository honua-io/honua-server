// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Postgres.Features.Infrastructure;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    private CoreParameterizedQuery BuildEncodedBinaryQuery(
        string encoderFunction,
        bool includeIndex,
        MetadataV2Resource resource,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType)
    {
        var spatialFilter = query.SpatialFilter;
        var isKnnQuery = spatialFilter.HasValue &&
                         spatialFilter.Value.SpatialRelationship == SpatialRelationship.NearestNeighbor;

        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();
            var geometrySelect = _geometryProcessor.GetGeometryOperand(
                geometryStorageType,
                layerSrid: query.SpatialReferenceSrid);

            if (query.OutputSrid.HasValue &&
                (!query.SpatialReferenceSrid.HasValue || query.OutputSrid.Value != query.SpatialReferenceSrid.Value))
            {
                geometrySelect = DatumTransformSql.BuildTransformExpression(geometrySelect, query.OutputSrid.Value, query.OutputDatumTransformation);
            }

            sql.Append("SELECT ");
            sql.Append(encoderFunction);
            sql.Append("(q");
            if (includeIndex)
            {
                sql.Append(", true");
            }

            sql.Append(CultureInfo.InvariantCulture, $", '{FeatureQueryEncoding.GeometryColumn}') FROM (SELECT ");
            sql.Append(DatabaseSchema.ObjectIdColumn);
            sql.Append(", ");
            sql.Append(geometrySelect);
            sql.Append(" AS ");
            sql.Append(FeatureQueryEncoding.GeometryColumn);

            AppendEncodedBinaryAttributeColumns(sql, resource, query, ref paramIndex, parameters);

            sql.Append(CultureInfo.InvariantCulture, $" FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");
            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);
            AppendOrderByClause(sql, query, ref paramIndex, parameters);
            AppendKnnOrdering(sql, isKnnQuery, spatialFilter, query, geometryStorageType, ref paramIndex);
            AppendPagination(sql, isKnnQuery, query, spatialFilter, ref paramIndex);
            sql.Append(") q");

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    private static void AppendEncodedBinaryAttributeColumns(
        StringBuilder sql,
        MetadataV2Resource resource,
        FeatureQuery query,
        ref int paramIndex,
        List<object> parameters)
    {
        foreach (var field in GetEncodedBinaryAttributeFields(resource, query))
        {
            if (!IsValidFieldName(field.Name))
            {
                throw new ArgumentException($"Invalid field name for binary projection: {field.Name}");
            }

            sql.Append(", ");
            sql.Append(BuildEncodedBinaryAttributeExpression(field, ref paramIndex, parameters));
            sql.Append(" AS ");
            sql.Append(QuoteIdentifier(field.Name));
        }
    }

    private static IEnumerable<MetadataV2Field> GetEncodedBinaryAttributeFields(MetadataV2Resource resource, FeatureQuery query)
    {
        var includeAllFields = !query.OutFields.HasValue || query.OutFields.Value.IsDefaultOrEmpty;
        if (includeAllFields)
        {
            return resource.SchemaFields
                .Where(static field => field.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography))
                .Where(field => !IsObjectIdField(field.Name));
        }

        var requestedOutFields = query.OutFields.GetValueOrDefault();
        var requestedFields = new HashSet<string>(requestedOutFields, StringComparer.OrdinalIgnoreCase);
        if (requestedFields.Count == 0)
        {
            return [];
        }

        var availableFields = resource.SchemaFields
            .Where(static field => field.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography))
            .Where(field => !IsObjectIdField(field.Name))
            .ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);

        var orderedFields = new List<MetadataV2Field>(requestedOutFields.Length);
        foreach (var fieldName in requestedOutFields)
        {
            if (availableFields.TryGetValue(fieldName, out var field))
            {
                orderedFields.Add(field);
            }
        }

        return orderedFields;
    }

    private static string BuildEncodedBinaryAttributeExpression(
        MetadataV2Field field,
        ref int paramIndex,
        List<object> parameters)
    {
        var attributeValue = BuildAttributeValueExpression(field.Name, ref paramIndex, parameters);

        return field.Type switch
        {
            MetadataV2FieldType.Integer => $"NULLIF({attributeValue}, '')::integer",
            MetadataV2FieldType.BigInteger => $"NULLIF({attributeValue}, '')::bigint",
            MetadataV2FieldType.Float => $"NULLIF({attributeValue}, '')::real",
            MetadataV2FieldType.Double => $"NULLIF({attributeValue}, '')::double precision",
            MetadataV2FieldType.Boolean => $"NULLIF({attributeValue}, '')::boolean",
            MetadataV2FieldType.DateTime => $"NULLIF({attributeValue}, '')::timestamptz",
            MetadataV2FieldType.Date => $"NULLIF({attributeValue}, '')::date",
            MetadataV2FieldType.Time => $"NULLIF({attributeValue}, '')::time",
            MetadataV2FieldType.Uuid => $"NULLIF({attributeValue}, '')::uuid",
            MetadataV2FieldType.Json => BuildAttributeJsonExpression(field.Name, ref paramIndex, parameters),
            _ => attributeValue
        };
    }

    private static string BuildAttributeJsonExpression(string attributeName, ref int paramIndex, List<object> parameters)
    {
        var fieldParamIndex = paramIndex++;
        parameters.Add(attributeName);
        return $"{DatabaseSchema.AttributesColumn} -> ${fieldParamIndex}";
    }

    private static bool IsObjectIdField(string fieldName)
    {
        var canonicalName = DatabaseSchema.MapFieldName(fieldName);
        return canonicalName.Equals(DatabaseSchema.ObjectIdColumn, StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private CoreParameterizedQuery BuildOptimizedSelectWithWindowCountQuery(
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        string geometrySelect,
        bool aliasGeometry)
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();
            var geometryProjection = aliasGeometry
                ? $"{geometrySelect} AS {FeatureQueryEncoding.GeometryColumn}"
                : geometrySelect;
            var attributesSelect = BuildAttributesSelectExpression(query, ref paramIndex, parameters);

            sql.Append(CultureInfo.InvariantCulture, $@"
                SELECT {DatabaseSchema.ObjectIdColumn}, {geometryProjection}, {attributesSelect} AS {DatabaseSchema.AttributesColumn}, COUNT(*) OVER() as {FeatureQueryEncoding.InternalTotalCountColumn}
                FROM {_tableName}
                WHERE {DatabaseSchema.LayerIdColumn} = $1");

            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);
            AppendOrderByClause(sql, query, ref paramIndex, parameters);

            if (query.Limit.HasValue)
            {
                sql.Append(CultureInfo.InvariantCulture, $" LIMIT ${paramIndex++}");
                parameters.Add(query.Limit.Value);
            }

            if (query.Offset.HasValue)
            {
                sql.Append(CultureInfo.InvariantCulture, $" OFFSET ${paramIndex++}");
                parameters.Add(query.Offset.Value);
            }

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }
}
