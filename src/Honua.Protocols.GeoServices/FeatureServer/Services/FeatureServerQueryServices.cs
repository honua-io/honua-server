// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

internal sealed class FeatureServerQueryServices(
    IQueryFormatter queryFormatter,
    IFeatureQueryValidator queryValidator,
    FeatureServerSpatialReferenceResolver spatialReferenceResolver) : IFeatureServerQueryServices
{
    private readonly IQueryFormatter _queryFormatter = queryFormatter ?? throw new ArgumentNullException(nameof(queryFormatter));
    private readonly IFeatureQueryValidator _queryValidator = queryValidator ?? throw new ArgumentNullException(nameof(queryValidator));
    private readonly FeatureServerSpatialReferenceResolver _spatialReferenceResolver =
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

    public Task<int?> ParseSridAsync(string? srValue, CancellationToken cancellationToken = default)
        => _spatialReferenceResolver.ParseSridAsync(srValue, cancellationToken);

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
        string[]? outFields,
        bool suppressObjectId = false,
        bool returnCentroid = false,
        int? requestedOutputSrid = null)
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
            outFields,
            suppressObjectId,
            returnCentroid,
            requestedOutputSrid);
}
