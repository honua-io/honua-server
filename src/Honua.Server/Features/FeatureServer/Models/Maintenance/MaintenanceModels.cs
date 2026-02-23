// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.FeatureServer.Models;

/// <summary>
/// Request model for the append endpoint.
/// </summary>
public sealed class AppendRequest
{
    /// <summary>
    /// Features to append as a JSON array.
    /// </summary>
    [JsonPropertyName("edits")]
    public string? Edits { get; set; }

    /// <summary>
    /// The upload format: json or csv.
    /// </summary>
    [JsonPropertyName("sourceFormat")]
    public string? SourceFormat { get; set; }

    /// <summary>
    /// How to handle duplicate features: skip, update.
    /// </summary>
    [JsonPropertyName("upsert")]
    public bool Upsert { get; set; }

    /// <summary>
    /// Layer identifier for service-level append.
    /// </summary>
    [JsonPropertyName("layerId")]
    public int? LayerId { get; set; }

    /// <summary>
    /// Output format parameter.
    /// </summary>
    [JsonPropertyName("f")]
    public string? F { get; set; }
}

/// <summary>
/// Response model for the append endpoint.
/// </summary>
public sealed class AppendResponse
{
    /// <summary>
    /// Indicates if the operation was successful.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Number of features appended.
    /// </summary>
    [JsonPropertyName("numFeaturesAppended")]
    public int NumFeaturesAppended { get; set; }

    /// <summary>
    /// Number of features that failed.
    /// </summary>
    [JsonPropertyName("numFeaturesFailed")]
    public int NumFeaturesFailed { get; set; }
}

/// <summary>
/// Request model for the calculate endpoint.
/// </summary>
public sealed class CalculateRequest
{
    /// <summary>
    /// WHERE clause to filter features for calculation.
    /// </summary>
    [JsonPropertyName("where")]
    public string? Where { get; set; }

    /// <summary>
    /// Array of field calculation expressions.
    /// </summary>
    [JsonPropertyName("calcExpression")]
    public string? CalcExpression { get; set; }

    /// <summary>
    /// SQL expression type: standard or field.
    /// </summary>
    [JsonPropertyName("sqlFormat")]
    public string? SqlFormat { get; set; }

    /// <summary>
    /// Output format parameter.
    /// </summary>
    [JsonPropertyName("f")]
    public string? F { get; set; }
}

/// <summary>
/// Response model for the calculate endpoint.
/// </summary>
public sealed class CalculateResponse
{
    /// <summary>
    /// Indicates if the operation was successful.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Number of features updated by the calculation.
    /// </summary>
    [JsonPropertyName("updatedFeatureCount")]
    public int UpdatedFeatureCount { get; set; }
}

/// <summary>
/// Response model for queryDomains endpoint.
/// </summary>
public sealed class QueryDomainsResponse
{
    /// <summary>
    /// Array of domain definitions for the service.
    /// </summary>
    [JsonPropertyName("domains")]
    public DomainInfo[]? Domains { get; set; }
}

/// <summary>
/// Information about a coded-value domain.
/// </summary>
public sealed class DomainInfo
{
    /// <summary>
    /// Domain type (codedValue, range, inherited).
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// Domain name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Field name this domain applies to.
    /// </summary>
    [JsonPropertyName("fieldName")]
    public string? FieldName { get; set; }

    /// <summary>
    /// Layer identifier this domain belongs to.
    /// </summary>
    [JsonPropertyName("layerId")]
    public int LayerId { get; set; }
}

/// <summary>
/// Request model for the validateSQL endpoint.
/// </summary>
public sealed class ValidateSqlRequest
{
    /// <summary>
    /// SQL WHERE clause to validate.
    /// </summary>
    [JsonPropertyName("where")]
    public string? Where { get; set; }

    /// <summary>
    /// Output format parameter.
    /// </summary>
    [JsonPropertyName("f")]
    public string? F { get; set; }
}

/// <summary>
/// Response model for the validateSQL endpoint.
/// </summary>
public sealed class ValidateSqlResponse
{
    /// <summary>
    /// Indicates if the SQL expression is valid.
    /// </summary>
    [JsonPropertyName("isValidSQL")]
    public bool IsValidSql { get; set; }

    /// <summary>
    /// Error message if the SQL expression is invalid.
    /// </summary>
    [JsonPropertyName("validationError")]
    public string? ValidationError { get; set; }
}
