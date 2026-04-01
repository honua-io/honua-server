// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Features.OData.Models;

/// <summary>
/// Represents an OData batch request containing multiple operations.
/// Supports both JSON and multipart batch formats per OData v4 spec.
/// </summary>
public sealed class ODataBatchRequest
{
    /// <summary>
    /// Collection of requests in the batch.
    /// </summary>
    [JsonPropertyName("requests")]
    public required ImmutableArray<ODataBatchRequestItem> Requests { get; init; }
}

/// <summary>
/// Represents a single request within an OData batch.
/// </summary>
public sealed class ODataBatchRequestItem
{
    /// <summary>
    /// Unique identifier for this request within the batch.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// HTTP method (GET, POST, PATCH, DELETE).
    /// </summary>
    [JsonPropertyName("method")]
    public required string Method { get; init; }

    /// <summary>
    /// Relative URL for the request (e.g., "Features(1,100)").
    /// </summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>
    /// Optional request headers.
    /// </summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Optional request body for POST/PATCH operations.
    /// </summary>
    [JsonPropertyName("body")]
    public Dictionary<string, object?>? Body { get; init; }

    /// <summary>
    /// Optional atomicity group ID. Requests in the same group are executed atomically.
    /// </summary>
    [JsonPropertyName("atomicityGroup")]
    public string? AtomicityGroup { get; init; }

    /// <summary>
    /// Optional array of request IDs this request depends on.
    /// </summary>
    [JsonPropertyName("dependsOn")]
    public string[]? DependsOn { get; init; }
}

/// <summary>
/// Represents an OData batch response containing results for all operations.
/// </summary>
public sealed class ODataBatchResponse
{
    /// <summary>
    /// Collection of responses for each request in the batch.
    /// </summary>
    [JsonPropertyName("responses")]
    public required ODataBatchResponseItem[] Responses { get; init; }
}

/// <summary>
/// Represents a single response within an OData batch response.
/// </summary>
public sealed class ODataBatchResponseItem
{
    /// <summary>
    /// ID matching the request this response corresponds to.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// HTTP status code of the response.
    /// </summary>
    [JsonPropertyName("status")]
    public required int Status { get; init; }

    /// <summary>
    /// Response headers.
    /// </summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Response body (can be a feature, error, or null).
    /// </summary>
    [JsonPropertyName("body")]
    public object? Body { get; init; }

    internal Feature? MutationFeature { get; init; }
}

/// <summary>
/// Represents an OData aggregation request using $apply.
/// </summary>
public sealed class ODataAggregationResult
{
    /// <summary>
    /// OData context URL.
    /// </summary>
    [JsonPropertyName("@odata.context")]
    public string? Context { get; init; }

    /// <summary>
    /// Aggregation result values.
    /// </summary>
    [JsonPropertyName("value")]
    public required object[] Value { get; init; }
}

/// <summary>
/// Represents a single aggregation group result.
/// </summary>
public sealed class AggregationGroup
{
    /// <summary>
    /// Grouping key values.
    /// </summary>
    [JsonPropertyName("groupBy")]
    public Dictionary<string, object?>? GroupBy { get; init; }

    /// <summary>
    /// Aggregated values.
    /// </summary>
    [JsonPropertyName("aggregates")]
    public Dictionary<string, object?>? Aggregates { get; init; }
}

/// <summary>
/// Represents an OData response with expanded related entities.
/// </summary>
public sealed class ODataExpandedResponse
{
    /// <summary>
    /// OData context URL.
    /// </summary>
    [JsonPropertyName("@odata.context")]
    public string? Context { get; init; }

    /// <summary>
    /// Total count of items (when $count=true).
    /// </summary>
    [JsonPropertyName("@odata.count")]
    public long? Count { get; init; }

    /// <summary>
    /// Collection of data items with expanded relations.
    /// </summary>
    [JsonPropertyName("value")]
    public required object[] Value { get; init; }
}

/// <summary>
/// Represents the result of parsing $apply aggregation expression.
/// </summary>
public readonly record struct ParsedAggregation
{
    /// <summary>
    /// Type of aggregation operation (aggregate, groupby, filter, etc.).
    /// </summary>
    public required AggregationType Type { get; init; }

    /// <summary>
    /// Fields to group by (for groupby operations).
    /// </summary>
    public ImmutableArray<string>? GroupByFields { get; init; }

    /// <summary>
    /// Aggregate expressions (sum, avg, min, max, count).
    /// </summary>
    public ImmutableArray<AggregateExpression>? Aggregates { get; init; }

    /// <summary>
    /// Filter to apply before aggregation.
    /// </summary>
    public string? FilterExpression { get; init; }
}

/// <summary>
/// Types of OData aggregation operations.
/// </summary>
public enum AggregationType
{
    /// <summary>
    /// Simple aggregation without grouping.
    /// </summary>
    Aggregate,

    /// <summary>
    /// Grouping with optional aggregation.
    /// </summary>
    GroupBy,

    /// <summary>
    /// Filter transformation.
    /// </summary>
    Filter,

    /// <summary>
    /// Compute new properties.
    /// </summary>
    Compute
}

/// <summary>
/// Represents a single aggregate expression.
/// </summary>
public readonly record struct AggregateExpression
{
    /// <summary>
    /// Source field name.
    /// </summary>
    public required string Field { get; init; }

    /// <summary>
    /// Aggregation function (sum, avg, min, max, count, countdistinct).
    /// </summary>
    public required string Function { get; init; }

    /// <summary>
    /// Alias for the result.
    /// </summary>
    public required string Alias { get; init; }
}

/// <summary>
/// Represents an OData search result.
/// </summary>
public sealed class ODataSearchResult
{
    /// <summary>
    /// OData context URL.
    /// </summary>
    [JsonPropertyName("@odata.context")]
    public string? Context { get; init; }

    /// <summary>
    /// Total count of matching items.
    /// </summary>
    [JsonPropertyName("@odata.count")]
    public long? Count { get; init; }

    /// <summary>
    /// Search result items.
    /// </summary>
    [JsonPropertyName("value")]
    public required object[] Value { get; init; }
}
