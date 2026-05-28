// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;

internal sealed class FeatureServerQueryServices(
    IQueryFormatter queryFormatter,
    IFeatureQueryValidator queryValidator,
    SpatialReferenceResolver spatialReferenceResolver) : IFeatureServerQueryServices
{
    private readonly IQueryFormatter _queryFormatter = queryFormatter ?? throw new ArgumentNullException(nameof(queryFormatter));
    private readonly IFeatureQueryValidator _queryValidator = queryValidator ?? throw new ArgumentNullException(nameof(queryValidator));
    private readonly SpatialReferenceResolver _spatialReferenceResolver =
        spatialReferenceResolver ?? throw new ArgumentNullException(nameof(spatialReferenceResolver));

    public QueryValidationResult ValidateQueryLimits(QueryParameters queryParams)
        => _queryValidator.ValidateQueryLimits(queryParams);

    public RelatedRecordsValidationResult ValidateRelatedRecordsLimits(QueryRelatedRecordsParameters queryParams)
        => _queryValidator.ValidateRelatedRecordsLimits(queryParams);

    public Task<int?> ResolveSridAsync(
        string? srValue,
        GeoServicesSpatialReference? geometrySpatialReference,
        CancellationToken cancellationToken = default)
        => _spatialReferenceResolver.ResolveSridAsync(srValue, geometrySpatialReference, cancellationToken);

    public ValueTask<(object Response, string ContentType)> FormatQueryResultAsync(
        QueryResult<Feature> result,
        MetadataV2Resource resource,
        string format,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        int? geometryPrecision,
        double? maxAllowableOffset,
        string[]? outFields)
        => _queryFormatter.FormatQueryResultAsync(
            result,
            resource,
            format,
            returnGeometry,
            outputSrid,
            returnZ,
            returnM,
            geometryPrecision,
            maxAllowableOffset,
            outFields);
}
