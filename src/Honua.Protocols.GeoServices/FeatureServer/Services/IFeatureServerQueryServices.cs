// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

internal interface IFeatureServerQueryServices
{
    QueryValidationResult ValidateQueryLimits(QueryParameters queryParams);

    RelatedRecordsValidationResult ValidateRelatedRecordsLimits(QueryRelatedRecordsParameters queryParams);

    Task<int?> ResolveSridAsync(
        string? srValue,
        GeoServicesSpatialReference? geometrySpatialReference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses a spatial-reference string to its raw (pre-normalization) SRID, without registry
    /// validation. Used to recover a client-requested Web Mercator alias for response echo.
    /// </summary>
    Task<int?> ParseSridAsync(string? srValue, CancellationToken cancellationToken = default);

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
        string[]? outFields,
        bool suppressObjectId = false,
        bool returnCentroid = false,
        int? requestedOutputSrid = null);
}
