// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;

internal sealed class FeatureServerQueryServices(
    IQueryFormatter queryFormatter,
    IFeatureQueryValidator queryValidator,
    IFilterExpressionTranslator filterExpressionTranslator,
    SpatialReferenceResolver spatialReferenceResolver) : IFeatureServerQueryServices
{
    private readonly IQueryFormatter _queryFormatter = queryFormatter ?? throw new ArgumentNullException(nameof(queryFormatter));
    private readonly IFeatureQueryValidator _queryValidator = queryValidator ?? throw new ArgumentNullException(nameof(queryValidator));
    private readonly IFilterExpressionTranslator _filterExpressionTranslator =
        filterExpressionTranslator ?? throw new ArgumentNullException(nameof(filterExpressionTranslator));
    private readonly SpatialReferenceResolver _spatialReferenceResolver =
        spatialReferenceResolver ?? throw new ArgumentNullException(nameof(spatialReferenceResolver));

    public QueryValidationResult ValidateQueryLimits(QueryParameters queryParams)
        => _queryValidator.ValidateQueryLimits(queryParams);

    public RelatedRecordsValidationResult ValidateRelatedRecordsLimits(QueryRelatedRecordsParameters queryParams)
        => _queryValidator.ValidateRelatedRecordsLimits(queryParams);

    public SqlFragment? TranslateFilter(FilterExpression filterExpression, LayerDefinition layer)
        => _filterExpressionTranslator.Translate(filterExpression, layer);

    public Task<int?> ResolveSridAsync(
        string? srValue,
        GeoServicesSpatialReference? geometrySpatialReference,
        CancellationToken cancellationToken = default)
        => _spatialReferenceResolver.ResolveSridAsync(srValue, geometrySpatialReference, cancellationToken);

    public ValueTask<(object Response, string ContentType)> FormatQueryResultAsync(
        QueryResult<Feature> result,
        LayerDefinition layer,
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
            layer,
            format,
            returnGeometry,
            outputSrid,
            returnZ,
            returnM,
            geometryPrecision,
            maxAllowableOffset,
            outFields);
}
