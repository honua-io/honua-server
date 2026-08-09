// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Postgres.Features.FeatureStore.Services;

// FlatGeobuf / Geobuf output for source-backed (provider-routed) PostGIS layers
// (honua-server#1938). The canonical PostgresFeatureStoreRefactored reader implements
// both markers against the shared 'features' table; this storage-mapped reader is the
// production read path for any layer compiled through the Metadata v2 compat snapshot,
// and previously threw NotSupportedException for FlatGeobuf and lacked the Geobuf
// capability entirely, so the FeatureServer emitted a clean 400 even though the
// underlying PostGIS table fully supports ST_AsFlatGeobuf / ST_AsGeobuf.
//
// The encoders are PostGIS aggregates over a row whose geometry column carries the
// name passed as the final argument; we build an inner SELECT projecting objectid, the
// (optionally reprojected) geometry as a real geometry column, and each requested
// attribute as a typed column — mirroring FeatureQueryBuilder.BuildEncodedBinaryQuery on
// the native path — then aggregate it with ST_AsFlatGeobuf / ST_AsGeobuf, honoring the
// jsonb attributes projection, layer discriminator, output SRID, filters and paging.
internal sealed partial class PostgresStorageMappedFeatureReader : IFlatGeobufFeatureStore, IGeobufFeatureStore
{
    public async Task<byte[]?> QueryFlatGeobufAsyncCore(
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        if (_geometryColumn == null)
        {
            // FlatGeobuf is a geometry-bearing format; a non-spatial source layer has
            // nothing to encode. Returning null yields an empty success response rather
            // than producing an invalid payload.
            return null;
        }

        var sql = BuildEncodedBinarySelect("ST_AsFlatGeobuf", includeIndex: true, query);
        return await ExecuteEncodedBinaryQueryAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]?> QueryGeobufAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        if (_geometryColumn == null)
        {
            return null;
        }

        var sql = BuildEncodedBinarySelect("ST_AsGeobuf", includeIndex: false, query);
        return await ExecuteEncodedBinaryQueryAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]?> ExecuteEncodedBinaryQueryAsync(
        SqlBuilder sql,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateReadCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            return null;
        }

        return reader.GetFieldValue<byte[]>(0);
    }

    private SqlBuilder BuildEncodedBinarySelect(string encoderFunction, bool includeIndex, FeatureQuery query)
    {
        var sql = new SqlBuilder();
        var geometryExpression = BuildGeometryExpression(query);
        var indexArgument = includeIndex ? ", true" : string.Empty;

        sql.Append(CultureInfo.InvariantCulture, $"SELECT {encoderFunction}(q{indexArgument}, '{FeatureQueryEncoding.GeometryColumn}') FROM (SELECT ");
        sql.Append(CultureInfo.InvariantCulture, $"{_primaryKeyColumn}::bigint AS {ValidateAndQuoteIdentifier(FieldNames.ObjectId)}");
        sql.Append(CultureInfo.InvariantCulture, $", {geometryExpression} AS {ValidateAndQuoteIdentifier(FeatureQueryEncoding.GeometryColumn)}");
        AppendEncodedBinaryAttributeColumns(sql, query);
        sql.Append(CultureInfo.InvariantCulture, $" FROM {_qualifiedTableName}");
        AppendFilter(sql, query);
        AppendOrderBy(sql, query);
        AppendPagination(sql, query);
        sql.Append(CultureInfo.InvariantCulture, ") q");
        return sql;
    }

    private void AppendEncodedBinaryAttributeColumns(SqlBuilder sql, FeatureQuery query)
    {
        if (query.ExcludeAttributes)
        {
            return;
        }

        foreach (var field in ResolveAttributeFields(query))
        {
            sql.Append(CultureInfo.InvariantCulture, $", {BuildEncodedBinaryAttributeExpression(field)} AS {ValidateAndQuoteIdentifier(field.Name)}");
        }
    }

    // Source columns may live in an attributes JSONB blob (text accessor) or as physical
    // columns; ResolveColumnExpression returns the right SQL either way. Cast the value to
    // the field's declared SQL type so the encoder emits correctly-typed FlatGeobuf/Geobuf
    // columns (an Integer field becomes int, not text), matching the native path's
    // BuildEncodedBinaryAttributeExpression. The NULLIF(..., '') guard mirrors the native
    // path so empty text from a JSONB accessor does not break a numeric/temporal cast.
    private string BuildEncodedBinaryAttributeExpression(MetadataV2Field field)
    {
        var column = ResolveColumnExpression(field.Name);
        var nullableText = $"NULLIF(({column})::text, '')";

        return field.Type switch
        {
            MetadataV2FieldType.Integer => $"{nullableText}::integer",
            MetadataV2FieldType.BigInteger => $"{nullableText}::bigint",
            MetadataV2FieldType.Float => $"{nullableText}::real",
            MetadataV2FieldType.Double => $"{nullableText}::double precision",
            MetadataV2FieldType.Boolean => $"{nullableText}::boolean",
            MetadataV2FieldType.DateTime => $"{nullableText}::timestamptz",
            MetadataV2FieldType.Date => $"{nullableText}::date",
            MetadataV2FieldType.Time => $"{nullableText}::time",
            MetadataV2FieldType.Uuid => $"{nullableText}::uuid",
            _ => $"({column})::text"
        };
    }
}
