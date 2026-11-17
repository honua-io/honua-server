// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Db.Postgres.Features.Admin;

// Chooses the published field type for a source column. An exact policy publishes numeric/decimal
// columns as Decimal and timestamptz columns as TimestampOffset; the columns it names as
// default-typed (the primary key, the temporal column, fields an earlier publish kept as DateTime)
// keep the default mapping, as does every column under the default policy.
internal sealed class PublishFieldTypePolicy
{
    private readonly HashSet<string>? _defaultTypedColumns;

    private PublishFieldTypePolicy(HashSet<string>? defaultTypedColumns)
    {
        _defaultTypedColumns = defaultTypedColumns;
    }

    public static PublishFieldTypePolicy Default { get; } = new(null);

    public static PublishFieldTypePolicy Exact(IEnumerable<string?> defaultTypedColumns)
    {
        ArgumentNullException.ThrowIfNull(defaultTypedColumns);
        return new(new HashSet<string>(
            defaultTypedColumns.Where(static name => !string.IsNullOrWhiteSpace(name)).Select(static name => name!.Trim()),
            StringComparer.OrdinalIgnoreCase));
    }

    public static PublishFieldTypePolicy ForPublish(LayerPublishRequest request, string? primaryKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.ExactTypes ? Exact([request.TemporalColumn, primaryKey]) : Default;
    }

    // A layer that already has exact fields keeps them on republish even when the request omits
    // the flag; exactLayerDateTimeFields is null for a layer without exact fields.
    public static PublishFieldTypePolicy ForRewrite(
        LayerPublishRequest request, IReadOnlyList<string>? exactLayerDateTimeFields, string primaryKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.ExactTypes || exactLayerDateTimeFields is not null
            ? Exact([.. exactLayerDateTimeFields ?? [], request.TemporalColumn, primaryKey])
            : Default;
    }

    public static PublishFieldTypePolicy ForValidation(TablePublishValidationRequest request, IEnumerable<ColumnInfo> columns)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(columns);
        return request.ExactTypes
            ? Exact([
                .. columns.Where(static column => column.IsPrimaryKey).Select(static column => column.Name),
                request.PrimaryKey,
                request.TemporalColumn])
            : Default;
    }

    public MetadataV2FieldType Map(string columnName, string dataType)
        => _defaultTypedColumns is null || _defaultTypedColumns.Contains(columnName)
            ? MapPostgresType(dataType)
            : MapExactPostgresType(dataType);

    public static MetadataV2FieldType MapExactPostgresType(string dataType)
    {
        ArgumentNullException.ThrowIfNull(dataType);
        return dataType.Trim().ToLowerInvariant() switch
        {
            "numeric" or "decimal" => MetadataV2FieldType.Decimal,
            "timestamp with time zone" => MetadataV2FieldType.TimestampOffset,
            _ => MapPostgresType(dataType)
        };
    }

    public static MetadataV2FieldType MapPostgresType(string dataType)
    {
        ArgumentNullException.ThrowIfNull(dataType);
        return dataType.Trim().ToLowerInvariant() switch
        {
            "smallint" => MetadataV2FieldType.Integer,
            "integer" => MetadataV2FieldType.Integer,
            "bigint" => MetadataV2FieldType.BigInteger,
            "real" => MetadataV2FieldType.Float,
            "double precision" => MetadataV2FieldType.Double,
            "numeric" => MetadataV2FieldType.Double,
            "decimal" => MetadataV2FieldType.Double,
            "boolean" => MetadataV2FieldType.Boolean,
            "date" => MetadataV2FieldType.Date,
            "timestamp without time zone" => MetadataV2FieldType.DateTime,
            "timestamp with time zone" => MetadataV2FieldType.DateTime,
            "time without time zone" => MetadataV2FieldType.Time,
            "time with time zone" => MetadataV2FieldType.Time,
            "uuid" => MetadataV2FieldType.Uuid,
            "json" => MetadataV2FieldType.Json,
            "jsonb" => MetadataV2FieldType.Json,
            "bytea" => MetadataV2FieldType.Binary,
            _ => MetadataV2FieldType.String
        };
    }
}
