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
    internal static (byte[] Payload, string ContentType) FormatPage(
        QueryResult<Feature> result,
        MetadataV2Object resource,
        GeometryLimits geometryLimits,
        string[]? outFields = null,
        ILogger? logger = null,
        GeoParquetLimits? encodingLimits = null)
    {
        var objectIdFieldName = resource.FindPrimaryIdField()?.Name ?? FieldNames.ObjectId;
        var layerSrid = resource.ReadSrid() ?? 4326;
        return GeoParquetFeatureWriter.FormatAsGeoParquet(
            result,
            resource,
            objectIdFieldName,
            returnGeometry: true,
            outputSrid: layerSrid,
            returnZ: true,
            returnM: false,
            geometryLimits,
            outFields,
            logger,
            encodingLimits);
    }
}
