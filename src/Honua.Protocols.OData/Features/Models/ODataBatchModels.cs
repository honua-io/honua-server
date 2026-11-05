// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Protocols.OData.Models;

public sealed class ODataBatchRequest
{
    [JsonPropertyName("requests")]
    public required ImmutableArray<ODataBatchRequestItem> Requests { get; init; }
}

public sealed class ODataBatchRequestItem
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; init; }

    [JsonPropertyName("body")]
    public Dictionary<string, object?>? Body { get; init; }

    [JsonPropertyName("atomicityGroup")]
    public string? AtomicityGroup { get; init; }

    [JsonPropertyName("dependsOn")]
    public string[]? DependsOn { get; init; }
}

public sealed class ODataBatchResponse
{
    [JsonPropertyName("responses")]
    public required ODataBatchResponseItem[] Responses { get; init; }
}

public sealed class ODataBatchResponseItem
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("status")]
    public required int Status { get; init; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; init; }

    [JsonPropertyName("body")]
    public object? Body { get; init; }

    internal Feature? MutationFeature { get; init; }
}

public sealed class ODataAggregationResult
{
    [JsonPropertyName("@odata.context")]
    public string? Context { get; init; }

    [JsonPropertyName("value")]
    public required object[] Value { get; init; }
}

public sealed class AggregationGroup
{
    [JsonPropertyName("groupBy")]
    public Dictionary<string, object?>? GroupBy { get; init; }

    [JsonPropertyName("aggregates")]
    public Dictionary<string, object?>? Aggregates { get; init; }
}

public sealed class ODataExpandedResponse
{
    [JsonPropertyName("@odata.context")]
    public string? Context { get; init; }

    [JsonPropertyName("@odata.count")]
    public long? Count { get; init; }

    [JsonPropertyName("value")]
    public required object[] Value { get; init; }
}

public readonly record struct ParsedAggregation
{
    public required AggregationType Type { get; init; }

    public ImmutableArray<string>? GroupByFields { get; init; }

    public ImmutableArray<AggregateExpression>? Aggregates { get; init; }

    public string? FilterExpression { get; init; }
}

public enum AggregationType
{
    Aggregate,

    GroupBy,

    Filter,

    Compute
}

public readonly record struct AggregateExpression
{
    public required string Field { get; init; }

    public required string Function { get; init; }

    public required string Alias { get; init; }
}

public sealed class ODataSearchResult
{
    [JsonPropertyName("@odata.context")]
    public string? Context { get; init; }

    [JsonPropertyName("@odata.count")]
    public long? Count { get; init; }

    [JsonPropertyName("value")]
    public required object[] Value { get; init; }

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; init; }
}
