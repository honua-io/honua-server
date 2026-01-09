// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Validation;

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Validates and applies limits to feature query parameters
/// </summary>
internal sealed class FeatureQueryValidator : IFeatureQueryValidator
{
    private readonly ICommonQueryValidator _commonQueryValidator;
    private static readonly PaginationValidationOptions _featureQueryPagination =
        new(MinOffset: 0, MinLimit: 1, OffsetParameterName: "resultOffset", LimitParameterName: "resultRecordCount");

    public FeatureQueryValidator(ICommonQueryValidator commonQueryValidator)
    {
        _commonQueryValidator = commonQueryValidator ?? throw new ArgumentNullException(nameof(commonQueryValidator));
    }

    /// <inheritdoc/>
    public QueryValidationResult ValidateQueryLimits(QueryParameters queryParams)
    {
        var paginationResult = _commonQueryValidator.ValidateAndNormalizePagination(
            queryParams.ResultOffset,
            queryParams.ResultRecordCount,
            _featureQueryPagination);
        if (!paginationResult.IsValid)
        {
            return QueryValidationResult.Invalid(paginationResult.ErrorMessage ?? "Invalid pagination parameters.");
        }

        var pagination = paginationResult.Value!;

        // Create new validated parameters object
        var validatedParams = new QueryParameters
        {
            Where = queryParams.Where,
            OutFields = queryParams.OutFields,
            OrderByFields = queryParams.OrderByFields,
            ReturnGeometry = queryParams.ReturnGeometry,
            ReturnIdsOnly = queryParams.ReturnIdsOnly,
            ReturnCountOnly = queryParams.ReturnCountOnly,
            ReturnExtentOnly = queryParams.ReturnExtentOnly,
            ReturnCentroid = queryParams.ReturnCentroid,
            ReturnDistinctValues = queryParams.ReturnDistinctValues,
            F = queryParams.F,
            ResultOffset = pagination.Offset,
            ResultRecordCount = pagination.Limit,
            Geometry = queryParams.Geometry,
            InSr = queryParams.InSr,
            OutSr = queryParams.OutSr,
            GeometryType = queryParams.GeometryType,
            SpatialRel = queryParams.SpatialRel,
            Distance = queryParams.Distance,
            Units = queryParams.Units,
            NearestCount = queryParams.NearestCount,
            ReturnDistance = queryParams.ReturnDistance,
            Time = queryParams.Time,
            TimeRelation = queryParams.TimeRelation,
            ObjectIds = queryParams.ObjectIds
        };

        return QueryValidationResult.Valid(validatedParams);
    }

    /// <inheritdoc/>
    public RelatedRecordsValidationResult ValidateRelatedRecordsLimits(QueryRelatedRecordsParameters queryParams)
    {
        var paginationResult = _commonQueryValidator.ValidateAndNormalizePagination(
            null,
            queryParams.ResultRecordCount,
            _featureQueryPagination);
        if (!paginationResult.IsValid)
        {
            return RelatedRecordsValidationResult.Invalid(paginationResult.ErrorMessage ?? "Invalid record count.");
        }

        var recordCount = paginationResult.Value!.Limit;

        // Create new validated parameters object
        var validatedParams = new QueryRelatedRecordsParameters
        {
            ObjectIds = queryParams.ObjectIds,
            RelationshipId = queryParams.RelationshipId,
            OutFields = queryParams.OutFields,
            ReturnGeometry = queryParams.ReturnGeometry,
            F = queryParams.F,
            ResultRecordCount = recordCount,
            Where = queryParams.Where
        };

        return RelatedRecordsValidationResult.Valid(validatedParams);
    }
}
