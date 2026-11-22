// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Db.DuckDB.Features.Infrastructure;

internal static class DuckDBProviderOptions
{
    public const string ExternalSourcePath = "externalSourcePath";

    public const string ExternalSourceFormat = "externalSourceFormat";

    public const string ObjectStorageConnectionId = "objectStorageConnectionId";

    public const string ObjectStorageKey = "objectStorageKey";

    public const string BboxColumn = "bboxColumn";
}

internal sealed class DuckDBLayerMapping
{
    public required int LayerId { get; init; }

    public required string TableName { get; init; }

    public required string GeometryColumn { get; init; }

    public required string ObjectIdColumn { get; init; }

    public int Srid { get; init; } = SpatialConstants.DefaultSrid;

    public required IReadOnlyList<string> AttributeColumns { get; init; }

    public IReadOnlyDictionary<string, string> AttributeColumnTypes { get; init; } =
        new Dictionary<string, string>();

    public ImmutableArray<string> ExternalSourcePaths { get; init; } = [];

    public string? ExternalSourceFormat { get; init; }

    public Guid? ObjectStorageConnectionId { get; init; }

    public string? ObjectStorageKey { get; init; }

    public string? BboxColumn { get; init; }

    public bool IsExternalSource => !ExternalSourcePaths.IsDefaultOrEmpty || ObjectStorageConnectionId.HasValue;

    public bool SupportsBboxPushdown => !string.IsNullOrWhiteSpace(BboxColumn);

    public string QuotedTableName => DuckDBExternalSourceSql.QuoteIdentifier(TableName);

    public string QuotedGeometryColumn => DuckDBExternalSourceSql.QuoteIdentifier(GeometryColumn);

    public string QuotedObjectIdColumn => DuckDBExternalSourceSql.QuoteIdentifier(ObjectIdColumn);

    public string? QuotedBboxColumn => string.IsNullOrWhiteSpace(BboxColumn)
        ? null
        : DuckDBExternalSourceSql.QuoteIdentifier(BboxColumn);

    public static DuckDBLayerMapping FromStorage(
        int layerId,
        FeatureStorageMapping storage,
        IReadOnlyList<string> attributeColumns)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(attributeColumns);

        var errors = storage.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(
                $"Invalid storage mapping for DuckDB layer {layerId}: {string.Join("; ", errors)}",
                nameof(storage));
        }

        if (string.IsNullOrWhiteSpace(storage.GeometryColumn))
        {
            throw new ArgumentException(
                $"DuckDB layer {layerId} must configure a geometry column.",
                nameof(storage));
        }

        storage.ProviderOptions.TryGetValue(DuckDBProviderOptions.ExternalSourcePath, out var externalPath);
        storage.ProviderOptions.TryGetValue(DuckDBProviderOptions.ExternalSourceFormat, out var externalFormat);
        storage.ProviderOptions.TryGetValue(DuckDBProviderOptions.ObjectStorageConnectionId, out var rawConnId);
        storage.ProviderOptions.TryGetValue(DuckDBProviderOptions.ObjectStorageKey, out var objectStorageKey);
        storage.ProviderOptions.TryGetValue(DuckDBProviderOptions.BboxColumn, out var bboxColumn);

        if (!string.IsNullOrWhiteSpace(externalPath))
        {
            DuckDBOptionsValidator.ThrowIfUnsafeSourcePath(externalPath, layerId);
        }

        Guid? connectionId = null;
        if (!string.IsNullOrWhiteSpace(rawConnId))
        {
            if (!Guid.TryParse(rawConnId, out var parsed) || parsed == Guid.Empty)
            {
                throw new ArgumentException(
                    $"Invalid DuckDB objectStorageConnectionId '{rawConnId}' for layer {layerId}.",
                    nameof(storage));
            }

            connectionId = parsed;
        }

        if (!string.IsNullOrWhiteSpace(bboxColumn) && !FeatureFieldNameSyntax.IsValid(bboxColumn))
        {
            throw new ArgumentException(
                $"Invalid DuckDB bboxColumn '{bboxColumn}' for layer {layerId}.",
                nameof(storage));
        }

        return new DuckDBLayerMapping
        {
            LayerId = layerId,
            TableName = storage.TableName,
            GeometryColumn = storage.GeometryColumn,
            ObjectIdColumn = storage.PrimaryKeyColumn,
            Srid = storage.StorageSrid ?? SpatialConstants.DefaultSrid,
            AttributeColumns = attributeColumns,
            ExternalSourcePaths = string.IsNullOrWhiteSpace(externalPath) ? [] : [externalPath],
            ExternalSourceFormat = string.IsNullOrWhiteSpace(externalFormat)
                ? (string.IsNullOrWhiteSpace(externalPath) && !connectionId.HasValue ? null : "GeoParquet")
                : externalFormat,
            ObjectStorageConnectionId = connectionId,
            ObjectStorageKey = string.IsNullOrWhiteSpace(objectStorageKey) ? null : objectStorageKey,
            BboxColumn = string.IsNullOrWhiteSpace(bboxColumn) ? null : bboxColumn,
        };
    }
}
