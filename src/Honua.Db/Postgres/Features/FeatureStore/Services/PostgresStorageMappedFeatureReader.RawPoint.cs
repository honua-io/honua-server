// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

/// <summary>
/// GeoServices point JSON fast path for a source-backed reader. FeatureServer selects this
/// interface instead of materializing WKB and a feature dictionary for compact point JSON.
/// </summary>
internal sealed partial class PostgresStorageMappedFeatureReader : IPagedRawGeoServicesFeatureStore
{
    /// <inheritdoc />
    public async Task<PagedQueryResult<RawGeoServicesFeature>> QueryGeoServicesRawPointPageAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        _ = layerId;
        if (query.Distinct)
        {
            throw new NotSupportedException(
                "Distinct raw GeoServices point pages are not supported for source-backed PostGIS layers.");
        }

        query = await ApplyReadSecurityAsync(query, cancellationToken).ConfigureAwait(false);
        var probeLimit = query.Limit is > 0 and < int.MaxValue;
        var sql = BuildRawPointSelect(query, probeLimit);
        var features = new List<RawGeoServicesFeature>(
            probeLimit ? query.Limit!.Value + 1 : 16);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateReadCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            features.Add(ReadRawPoint(reader));
        }

        if (features.Count == 0)
        {
            return PagedQueryResult<RawGeoServicesFeature>.Empty();
        }

        var hasMoreResults = probeLimit && features.Count > query.Limit!.Value;
        if (hasMoreResults)
        {
            features.RemoveAt(features.Count - 1);
        }

        return PagedQueryResult<RawGeoServicesFeature>.Create(features.ToImmutableArray(), hasMoreResults);
    }

    private SqlBuilder BuildRawPointSelect(FeatureQuery query, bool probeLimit)
    {
        var sql = new SqlBuilder();
        var attributesSelect = query.ExcludeAttributes
            ? "NULL::text"
            : $"({BuildAttributesJsonbExpression(query, sql)})::text";
        var publicIdSelect = BuildRawPointPublicIdSelect(query, sql);
        var geometryExpression = BuildGeometryExpression(query);
        var xSelect = _geometryColumn == null
            ? "NULL::double precision"
            : $"CASE WHEN GeometryType({geometryExpression}) = 'POINT' THEN ST_X({geometryExpression}) ELSE NULL END";
        var ySelect = _geometryColumn == null
            ? "NULL::double precision"
            : $"CASE WHEN GeometryType({geometryExpression}) = 'POINT' THEN ST_Y({geometryExpression}) ELSE NULL END";

        sql.Append(CultureInfo.InvariantCulture, $"""
            SELECT {_primaryKeyColumn}::bigint AS objectid,
                   {publicIdSelect} AS public_id,
                   {attributesSelect} AS attributes,
                   {xSelect} AS x,
                   {ySelect} AS y
            """);

        if (query.Limit.HasValue || query.Offset.HasValue || IsNearestNeighborQuery(query))
        {
            // Page the source columns first, then project coordinates for that page only.
            AppendPagedFeatureSource(sql, query, probeLimit);
        }
        else
        {
            sql.Append(CultureInfo.InvariantCulture, $" FROM {BuildFeatureSource(query, sql)}");
            AppendFilter(sql, query);
            AppendOrderBy(sql, query);
        }

        return sql;
    }

    // The GeoServices writer emits a null public id as the numeric object id. A public id that
    // is the storage primary key takes that path. Any other field is projected as a JSON scalar
    // so a string object id keeps its stored type.
    private string BuildRawPointPublicIdSelect(FeatureQuery query, SqlBuilder sql)
    {
        var name = query.PublicIdAttributeName;
        if (string.IsNullOrWhiteSpace(name) ||
            name.Equals(_mapping.PrimaryKeyColumn, StringComparison.OrdinalIgnoreCase) ||
            IsObjectIdField(name))
        {
            return "NULL::text";
        }

        if (!string.IsNullOrWhiteSpace(_mapping.AttributesColumn))
        {
            var jsonbAccessor = ValidateAndQuoteIdentifier(_mapping.AttributesColumn);
            return $"({jsonbAccessor} -> {sql.AddParameter(name)}::text)::text";
        }

        return $"to_jsonb({ResolveColumnExpression(name, sql)})::text";
    }

    private static RawGeoServicesFeature ReadRawPoint(NpgsqlDataReader reader)
    {
        var id = reader.GetInt64(0);
        var publicIdJson = reader.IsDBNull(1) ? null : reader.GetString(1);
        var attributesJson = reader.IsDBNull(2) ? null : reader.GetString(2);
        double? x = reader.IsDBNull(3) ? null : reader.GetDouble(3);
        double? y = reader.IsDBNull(4) ? null : reader.GetDouble(4);
        return RawGeoServicesFeature.Create(id, publicIdJson, attributesJson, x, y);
    }
}
