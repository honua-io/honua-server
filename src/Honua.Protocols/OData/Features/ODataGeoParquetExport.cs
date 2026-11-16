// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.OData;

internal static class ODataGeoParquetExport
{
    internal static (ReadOnlyMemory<byte> Payload, string ContentType) FormatPage(
        QueryResult<Feature> result,
        MetadataV2Object resource,
        GeometryLimits geometryLimits,
        string[]? outFields = null,
        ILogger? logger = null,
        GeoParquetLimits? encodingLimits = null)
    {
        var (payload, contentType) = FormatPageStream(result, resource, new ODataGeoParquetStreamOptions
        {
            GeometryLimits = geometryLimits,
            OutFields = outFields,
            Logger = logger,
            EncodingLimits = encodingLimits
        });
        using (payload)
        using (var copy = new MemoryStream())
        {
            payload.CopyTo(copy);
            return (copy.ToArray(), contentType);
        }
    }

    internal static (Stream Payload, string ContentType) FormatPageStream(
        QueryResult<Feature> result,
        MetadataV2Object resource,
        ODataGeoParquetStreamOptions options)
    {
        var geometryLimits = options.GeometryLimits;
        var outFields = options.OutFields;
        var logger = options.Logger;
        var encodingLimits = options.EncodingLimits;
        var objectIdFieldName = resource.FindPrimaryIdField()?.Name ?? FieldNames.ObjectId;
        var layerSrid = resource.ReadSrid() ?? 4326;
        return GeoParquetFeatureWriter.FormatAsGeoParquetStream(result, resource, new GeoParquetStreamEncodingOptions
        {
            ObjectIdFieldName = objectIdFieldName,
            ReturnGeometry = true,
            OutputSrid = layerSrid,
            ReturnZ = true,
            ReturnM = false,
            GeometryLimits = geometryLimits,
            OutFields = outFields,
            Logger = logger,
            EncodingLimits = encodingLimits
        });
    }
}
