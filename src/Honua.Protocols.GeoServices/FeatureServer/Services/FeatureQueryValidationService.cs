// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Result of query parameter validation
/// </summary>
public sealed record QueryValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public QueryParameters? ValidatedParameters { get; init; }

    public static QueryValidationResult Valid(QueryParameters parameters) =>
        new() { IsValid = true, ValidatedParameters = parameters };

    public static QueryValidationResult Invalid(string errorMessage) =>
        new() { IsValid = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Result of related records validation
/// </summary>
public sealed record RelatedRecordsValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public QueryRelatedRecordsParameters? ValidatedParameters { get; init; }

    public static RelatedRecordsValidationResult Valid(QueryRelatedRecordsParameters parameters) =>
        new() { IsValid = true, ValidatedParameters = parameters };

    public static RelatedRecordsValidationResult Invalid(string errorMessage) =>
        new() { IsValid = false, ErrorMessage = errorMessage };
}

