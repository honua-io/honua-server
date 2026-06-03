// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Validation.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Infrastructure.Validation;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

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
        var effectiveResultRecordCount = queryParams.ResultRecordCount;
        if (!queryParams.ReturnIdsOnly &&
            !effectiveResultRecordCount.HasValue &&
            queryParams.ObjectIds is { Length: > 0 })
        {
            // ObjectIds queries should not be truncated by the default record-count limit.
            effectiveResultRecordCount = queryParams.ObjectIds.Length;
        }

        var paginationResult = _commonQueryValidator.ValidateAndNormalizePagination(
            queryParams.ResultOffset,
            effectiveResultRecordCount,
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
            ReturnZ = queryParams.ReturnZ,
            ReturnM = queryParams.ReturnM,
            ReturnTrueCurves = queryParams.ReturnTrueCurves,
            ReturnExceededLimitFeatures = queryParams.ReturnExceededLimitFeatures,
            F = queryParams.F,
            FormatSpecified = queryParams.FormatSpecified,
            ResultOffset = queryParams.ReturnIdsOnly ? null : pagination.Offset,
            ResultRecordCount = queryParams.ReturnIdsOnly ? null : pagination.Limit,
            Geometry = queryParams.Geometry,
            InSr = queryParams.InSr,
            InSrSpecified = queryParams.InSrSpecified,
            OutSr = queryParams.OutSr,
            OutSrSpecified = queryParams.OutSrSpecified,
            GeometryType = queryParams.GeometryType,
            SpatialRel = queryParams.SpatialRel,
            Distance = queryParams.Distance,
            Units = queryParams.Units,
            NearestCount = queryParams.NearestCount,
            ReturnDistance = queryParams.ReturnDistance,
            GeometryPrecision = queryParams.GeometryPrecision,
            MaxAllowableOffset = queryParams.MaxAllowableOffset,
            ResultType = queryParams.ResultType,
            OutStatistics = queryParams.OutStatistics,
            GroupByFieldsForStatistics = queryParams.GroupByFieldsForStatistics,
            Having = queryParams.Having,
            SqlFormat = queryParams.SqlFormat,
            GdbVersion = queryParams.GdbVersion,
            QuantizationParameters = queryParams.QuantizationParameters,
            DatumTransformation = queryParams.DatumTransformation,
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
            queryParams.ResultOffset,
            queryParams.ResultRecordCount,
            _featureQueryPagination);
        if (!paginationResult.IsValid)
        {
            return RelatedRecordsValidationResult.Invalid(paginationResult.ErrorMessage ?? "Invalid record count.");
        }

        var pagination = paginationResult.Value!;

        // Create new validated parameters object
        var validatedParams = new QueryRelatedRecordsParameters
        {
            ObjectIds = queryParams.ObjectIds,
            RelationshipId = queryParams.RelationshipId,
            OutFields = queryParams.OutFields,
            ReturnGeometry = queryParams.ReturnGeometry,
            F = queryParams.F,
            OutSr = queryParams.OutSr,
            ReturnZ = queryParams.ReturnZ,
            ReturnM = queryParams.ReturnM,
            GeometryPrecision = queryParams.GeometryPrecision,
            MaxAllowableOffset = queryParams.MaxAllowableOffset,
            GdbVersion = queryParams.GdbVersion,
            SqlFormat = queryParams.SqlFormat,
            HistoricMoment = queryParams.HistoricMoment,
            ReturnTrueCurves = queryParams.ReturnTrueCurves,
            ResultOffset = pagination.Offset,
            ResultRecordCount = pagination.Limit,
            Where = queryParams.Where,
            OrderByFields = queryParams.OrderByFields,
            ReturnCountOnly = queryParams.ReturnCountOnly
        };

        return RelatedRecordsValidationResult.Valid(validatedParams);
    }
}
