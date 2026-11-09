// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class AppendRequest
{
    [JsonPropertyName("edits")]
    public string? Edits { get; set; }

    [JsonPropertyName("sourceFormat")]
    public string? SourceFormat { get; set; }

    [JsonPropertyName("upsert")]
    public bool Upsert { get; set; }

    [JsonPropertyName("layerId")]
    public int? LayerId { get; set; }

    [JsonPropertyName("f")]
    public string? F { get; set; }
}

public sealed class AppendResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("numFeaturesAppended")]
    public int NumFeaturesAppended { get; set; }

    [JsonPropertyName("numFeaturesFailed")]
    public int NumFeaturesFailed { get; set; }
}

public sealed class CalculateRequest
{
    [JsonPropertyName("where")]
    public string? Where { get; set; }

    [JsonPropertyName("calcExpression")]
    public string? CalcExpression { get; set; }

    [JsonPropertyName("sqlFormat")]
    public string? SqlFormat { get; set; }

    [JsonPropertyName("f")]
    public string? F { get; set; }
}

public sealed class CalculateResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("updatedFeatureCount")]
    public int UpdatedFeatureCount { get; set; }
}

public sealed class QueryDomainsResponse
{
    [JsonPropertyName("domains")]
    public DomainInfo[]? Domains { get; set; }
}

public sealed class QueryRelationshipsResponse
{
    [JsonPropertyName("relationships")]
    public ServiceRelationshipInfo[]? Relationships { get; set; }
}

public sealed class ServiceRelationshipSummary
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class ServiceRelationshipInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("backwardPathLabel")]
    public string? BackwardPathLabel { get; set; }

    [JsonPropertyName("forwardPathLabel")]
    public string? ForwardPathLabel { get; set; }

    [JsonPropertyName("originLayerId")]
    public int OriginLayerId { get; set; }

    [JsonPropertyName("destinationLayerId")]
    public int DestinationLayerId { get; set; }

    [JsonPropertyName("originPrimaryKey")]
    public string? OriginPrimaryKey { get; set; }

    [JsonPropertyName("originForeignKey")]
    public string? OriginForeignKey { get; set; }

    [JsonPropertyName("destinationPrimaryKey")]
    public string? DestinationPrimaryKey { get; set; }

    [JsonPropertyName("cardinality")]
    public string? Cardinality { get; set; }

    [JsonPropertyName("attributed")]
    public bool Attributed { get; set; }

    [JsonPropertyName("composite")]
    public bool Composite { get; set; }

    [JsonPropertyName("rules")]
    public object[] Rules { get; set; } = [];

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public sealed class DomainInfo
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("fieldName")]
    public string? FieldName { get; set; }

    [JsonPropertyName("fieldType")]
    public string? FieldType { get; set; }

    [JsonPropertyName("layerId")]
    public int LayerId { get; set; }

    [JsonPropertyName("codedValues")]
    public DomainCodedValueInfo[]? CodedValues { get; set; }

    [JsonPropertyName("range")]
    public object[]? Range { get; set; }

    [JsonPropertyName("mergePolicy")]
    public string? MergePolicy { get; set; }

    [JsonPropertyName("splitPolicy")]
    public string? SplitPolicy { get; set; }
}

public sealed class DomainCodedValueInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("code")]
    public required object? Code { get; set; }
}

public sealed class ValidateSqlRequest
{
    [JsonPropertyName("where")]
    public string? Where { get; set; }

    [JsonPropertyName("f")]
    public string? F { get; set; }
}

public sealed class ValidateSqlResponse
{
    [JsonPropertyName("isValidSQL")]
    public bool IsValidSql { get; set; }

    [JsonPropertyName("validationError")]
    public string? ValidationError { get; set; }
}
