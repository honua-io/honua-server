// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Honua.Io.Export.Writers;

internal sealed record GeoParquetExportRequest
{
    public required string LayerName { get; init; }
    public required ExportField[] Fields { get; init; }
    public required ExportGeometryType GeometryType { get; init; }
    public required int OutputSrid { get; init; }
    public required LimitsOptions Limits { get; init; }
    public ILogger? Logger { get; init; }
}

internal static class GeoParquetExportWriter
{
    private const int CopyBufferBytes = 64 * 1024;

    public static async Task<int> WriteAsync(
        string outputPath,
        IAsyncEnumerable<Feature> features,
        GeoParquetExportRequest request,
        CancellationToken cancellationToken)
    {
        var (stream, writtenCount) = await EncodeStreamAsync(features, request, cancellationToken)
            .ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        await using (var destination = File.Create(outputPath))
        {
            await stream.CopyToAsync(destination, CopyBufferBytes, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return writtenCount;
    }

    public static async Task<(Stream Payload, int WrittenCount)> EncodeStreamAsync(
        IAsyncEnumerable<Feature> features,
        GeoParquetExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(request);

        var encodingLimits = GeoParquetLimits.ForArtifact(request.Limits.GeoParquet);
        var resource = BuildResource(request);
        var objectIdFieldName = ResolveObjectIdFieldName(request.Fields);
        var writtenCount = 0;
        var (payload, _) = await GeoParquetFeatureWriter.FormatAsGeoParquetStreamAsync(
            CountFeaturesAsync(cancellationToken),
            resource,
            new GeoParquetStreamEncodingOptions
            {
                ObjectIdFieldName = objectIdFieldName,
                ReturnGeometry = request.GeometryType != ExportGeometryType.None,
                OutputSrid = request.OutputSrid,
                ReturnZ = true,
                ReturnM = false,
                GeometryLimits = request.Limits.Geometry,
                Logger = request.Logger,
                EncodingLimits = encodingLimits
            },
            cancellationToken).ConfigureAwait(false);

        return (payload, writtenCount);

        async IAsyncEnumerable<Feature> CountFeaturesAsync([EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var feature in features.WithCancellation(token).ConfigureAwait(false))
            {
                writtenCount++;
                yield return feature;
            }
        }
    }

    private static MetadataV2Object BuildResource(GeoParquetExportRequest request)
    {
        var schemaFields = request.Fields
            .Select(static field => new MetadataV2Field
            {
                Name = field.Name,
                Type = MapToMetadataFieldType(field.Type),
                Nullable = field.Nullable
            })
            .ToArray();

        return new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = request.LayerName,
                Name = request.LayerName
            },
            Spatial = request.GeometryType == ExportGeometryType.None
                ? null
                : new MetadataV2ObjectSpatial
                {
                    GeometryType = MapToMetadataGeometryType(request.GeometryType),
                    SpatialReference = new MetadataV2SpatialReference { Srid = request.OutputSrid }
                },
            SchemaFields = schemaFields
        };
    }

    private static string ResolveObjectIdFieldName(ExportField[] fields)
    {
        foreach (var field in fields)
        {
            if (string.Equals(field.Name, FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase))
            {
                return field.Name;
            }
        }

        return FieldNames.ObjectId;
    }

    private static MetadataV2GeometryType MapToMetadataGeometryType(ExportGeometryType geometryType) => geometryType switch
    {
        ExportGeometryType.Point => MetadataV2GeometryType.Point,
        ExportGeometryType.MultiPoint => MetadataV2GeometryType.MultiPoint,
        ExportGeometryType.LineString => MetadataV2GeometryType.LineString,
        ExportGeometryType.MultiLineString => MetadataV2GeometryType.MultiLineString,
        ExportGeometryType.Polygon => MetadataV2GeometryType.Polygon,
        ExportGeometryType.MultiPolygon => MetadataV2GeometryType.MultiPolygon,
        ExportGeometryType.GeometryCollection => MetadataV2GeometryType.GeometryCollection,
        ExportGeometryType.Mixed => MetadataV2GeometryType.Mixed,
        _ => MetadataV2GeometryType.None
    };

    private static MetadataV2FieldType MapToMetadataFieldType(ExportFieldType fieldType) => fieldType switch
    {
        ExportFieldType.Integer => MetadataV2FieldType.Integer,
        ExportFieldType.BigInteger => MetadataV2FieldType.BigInteger,
        ExportFieldType.Double => MetadataV2FieldType.Double,
        ExportFieldType.Float => MetadataV2FieldType.Float,
        ExportFieldType.Boolean => MetadataV2FieldType.Boolean,
        ExportFieldType.DateTime => MetadataV2FieldType.DateTime,
        ExportFieldType.Date => MetadataV2FieldType.Date,
        ExportFieldType.Time => MetadataV2FieldType.Time,
        ExportFieldType.Json => MetadataV2FieldType.Json,
        ExportFieldType.Binary => MetadataV2FieldType.Binary,
        ExportFieldType.Uuid => MetadataV2FieldType.Uuid,
        _ => MetadataV2FieldType.String
    };
}
