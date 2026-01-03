// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.FeatureServer.Models;

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Validates and applies limits to feature query parameters
/// </summary>
public interface IFeatureQueryValidator
{
    /// <summary>
    /// Validates and applies query limits to the provided parameters
    /// </summary>
    /// <param name="queryParams">The query parameters to validate</param>
    /// <returns>Validation result with either validated parameters or error message</returns>
    QueryValidationResult ValidateQueryLimits(QueryParameters queryParams);

    /// <summary>
    /// Validates and applies related records query limits to the provided parameters
    /// </summary>
    /// <param name="queryParams">The related records query parameters to validate</param>
    /// <returns>Validation result with either validated parameters or error message</returns>
    RelatedRecordsValidationResult ValidateRelatedRecordsLimits(QueryRelatedRecordsParameters queryParams);
}
