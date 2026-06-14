// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Apache.Arrow;
using Apache.Arrow.Types;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// GeoServices FeatureServer adapter over the shared <see cref="GeoParquetFeatureWriter"/>.
/// Resolves the GeoServices object-id field semantics and delegates all GeoParquet
/// encoding to the shared writer so the wire format is not reimplemented per protocol.
/// </summary>
internal static class GeoParquetQueryFormatter
{
    /// <summary>
    /// Formats query result as GeoParquet using Metadata v2 resource metadata.
    /// </summary>
    public static (byte[] response, string contentType) FormatAsGeoParquet(
        QueryResult<Feature> result,
        MetadataV2Resource resource,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        string[]? outFields = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        return GeoParquetFeatureWriter.FormatAsGeoParquet(
            result,
            resource,
            objectIdFieldName,
            returnGeometry,
            outputSrid,
            returnZ,
            returnM,
            geometryLimits,
            outFields,
            logger);
    }

    internal static List<MetadataV2Field> ResolveSelectedFields(MetadataV2Resource resource, string[]? outFields)
    {
        var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        return GeoParquetFeatureWriter.ResolveSelectedFields(resource, objectIdFieldName, outFields);
    }

    internal static void EnsureSupportedCloudNativeGeometrySrid(bool includeGeometry, int srid, string formatLabel)
        => GeoParquetFeatureWriter.EnsureSupportedCloudNativeGeometrySrid(includeGeometry, srid, formatLabel);

    internal static void EnsureSupportedCloudNativeGeometryMeasures(bool includeGeometry, bool returnM, string formatLabel)
        => GeoParquetFeatureWriter.EnsureSupportedCloudNativeGeometryMeasures(includeGeometry, returnM, formatLabel);

    internal static List<(string name, IArrowType type)> DetectRuntimeFields(
        ImmutableArray<Feature> features,
        MetadataV2Resource resource)
        => GeoParquetFeatureWriter.DetectRuntimeFields(features, resource);

    internal static string MapGeometryTypeToGeoParquet(MetadataV2GeometryType geometryType, bool returnZ)
        => GeoParquetFeatureWriter.MapGeometryTypeToGeoParquet(geometryType, returnZ);

    internal static object? GetAttributeValue(Feature feature, string fieldName)
        => GeoParquetFeatureWriter.GetAttributeValue(feature, fieldName);

    internal static byte[]? ProcessGeometry(
        byte[]? geometryBytes,
        int outputSrid,
        GeometryLimits geometryLimits,
        bool returnZ,
        bool returnM,
        long featureId = 0,
        ILogger? logger = null)
        => GeoParquetFeatureWriter.ProcessGeometry(geometryBytes, outputSrid, geometryLimits, returnZ, returnM, featureId, logger);
}
