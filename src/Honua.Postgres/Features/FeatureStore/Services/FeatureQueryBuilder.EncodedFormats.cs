// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.Infrastructure;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    private CoreParameterizedQuery BuildEncodedBinaryQuery(
        string encoderFunction,
        bool includeIndex,
        LayerDefinition layer,
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
                geometrySelect = $"ST_Transform({geometrySelect}, {query.OutputSrid.Value})";
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

            AppendEncodedBinaryAttributeColumns(sql, layer, query, ref paramIndex, parameters);

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
        LayerDefinition layer,
        FeatureQuery query,
        ref int paramIndex,
        List<object> parameters)
    {
        foreach (var field in GetEncodedBinaryAttributeFields(layer, query))
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

    private static IEnumerable<FieldDefinition> GetEncodedBinaryAttributeFields(LayerDefinition layer, FeatureQuery query)
    {
        var includeAllFields = !query.OutFields.HasValue || query.OutFields.Value.IsDefaultOrEmpty;
        if (includeAllFields)
        {
            return layer.AttributeFieldsSpan
                .ToArray()
                .Where(field => !IsObjectIdField(field.Name));
        }

        var requestedOutFields = query.OutFields.GetValueOrDefault();
        var requestedFields = new HashSet<string>(requestedOutFields, StringComparer.OrdinalIgnoreCase);
        if (requestedFields.Count == 0)
        {
            return [];
        }

        var availableFields = layer.AttributeFieldsSpan
            .ToArray()
            .Where(field => !IsObjectIdField(field.Name))
            .ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);

        var orderedFields = new List<FieldDefinition>(requestedOutFields.Length);
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
        FieldDefinition field,
        ref int paramIndex,
        List<object> parameters)
    {
        var attributeValue = BuildAttributeValueExpression(field.Name, ref paramIndex, parameters);

        return field.Type switch
        {
            FieldType.Integer => $"NULLIF({attributeValue}, '')::integer",
            FieldType.BigInteger => $"NULLIF({attributeValue}, '')::bigint",
            FieldType.Float => $"NULLIF({attributeValue}, '')::real",
            FieldType.Double => $"NULLIF({attributeValue}, '')::double precision",
            FieldType.Boolean => $"NULLIF({attributeValue}, '')::boolean",
            FieldType.DateTime => $"NULLIF({attributeValue}, '')::timestamptz",
            FieldType.Date => $"NULLIF({attributeValue}, '')::date",
            FieldType.Time => $"NULLIF({attributeValue}, '')::time",
            FieldType.Uuid => $"NULLIF({attributeValue}, '')::uuid",
            FieldType.Json => BuildAttributeJsonExpression(field.Name, ref paramIndex, parameters),
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

            sql.Append(CultureInfo.InvariantCulture, $@"
                SELECT {DatabaseSchema.ObjectIdColumn}, {geometryProjection}, {DatabaseSchema.AttributesColumn}, COUNT(*) OVER() as {FeatureQueryEncoding.InternalTotalCountColumn}
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
