// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Server.Features.FeatureServer.Models;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Validates and applies limits to feature query parameters
/// </summary>
internal sealed class FeatureQueryValidator : IFeatureQueryValidator
{
    private readonly LimitsOptions _limitsOptions;

    public FeatureQueryValidator(IOptions<LimitsOptions> limitsOptions)
    {
        _limitsOptions = limitsOptions?.Value ?? throw new ArgumentNullException(nameof(limitsOptions));
    }

    /// <inheritdoc/>
    public QueryValidationResult ValidateQueryLimits(QueryParameters queryParams)
    {
        // Get the effective values (use defaults if not specified)
        var requestedRecordCount = queryParams.ResultRecordCount ?? _limitsOptions.Query.DefaultRecordCount;
        var requestedOffset = queryParams.ResultOffset ?? 0;

        // Apply limit constraints per MVP Plan (clamp to maximum allowed)
        var recordCount = Math.Min(requestedRecordCount, _limitsOptions.Query.MaxRecordCount);
        var offset = Math.Min(requestedOffset, _limitsOptions.Query.MaxOffset);

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
            F = queryParams.F,
            ResultOffset = offset,
            ResultRecordCount = recordCount,
            Geometry = queryParams.Geometry,
            InSr = queryParams.InSr,
            OutSr = queryParams.OutSr,
            GeometryType = queryParams.GeometryType,
            SpatialRel = queryParams.SpatialRel,
            Distance = queryParams.Distance,
            Units = queryParams.Units,
            NearestCount = queryParams.NearestCount,
            ReturnDistance = queryParams.ReturnDistance,
            ObjectIds = queryParams.ObjectIds
        };

        return QueryValidationResult.Success(validatedParams);
    }

    /// <inheritdoc/>
    public RelatedRecordsValidationResult ValidateRelatedRecordsLimits(QueryRelatedRecordsParameters queryParams)
    {
        // Get the effective value (use default if not specified)
        var requestedRecordCount = queryParams.ResultRecordCount ?? _limitsOptions.Query.DefaultRecordCount;

        // First validate that the original request is within allowed ranges
        if (requestedRecordCount > _limitsOptions.Query.MaxRecordCount)
        {
            return RelatedRecordsValidationResult.Failure($"Maximum record count: {_limitsOptions.Query.MaxRecordCount}");
        }

        // Apply limit constraints for related records queries (clamp to maximum allowed)
        var recordCount = Math.Min(requestedRecordCount, _limitsOptions.Query.MaxRecordCount);

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

        return RelatedRecordsValidationResult.Success(validatedParams);
    }
}
