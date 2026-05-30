// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;

internal interface IFeatureServerQueryServices
{
    QueryValidationResult ValidateQueryLimits(QueryParameters queryParams);

    RelatedRecordsValidationResult ValidateRelatedRecordsLimits(QueryRelatedRecordsParameters queryParams);

    Task<int?> ResolveSridAsync(
        string? srValue,
        GeoServicesSpatialReference? geometrySpatialReference,
        CancellationToken cancellationToken = default);

    ValueTask<(object Response, string ContentType)> FormatQueryResultAsync(
        QueryResult<Feature> result,
        MetadataV2Resource resource,
        string format,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        int? geometryPrecision,
        double? maxAllowableOffset,
        string[]? outFields);
}
