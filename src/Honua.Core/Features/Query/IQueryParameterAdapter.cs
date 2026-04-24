// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Features.Query;

/// <summary>
/// Protocol-specific adapter for converting protocol parameters to unified query.
/// </summary>
/// <typeparam name="TProtocolParams">Protocol-specific parameter type</typeparam>
public interface IQueryParameterAdapter<in TProtocolParams>
{
    /// <summary>
    /// Converts protocol-specific parameters to a unified query.
    /// </summary>
    /// <param name="parameters">Protocol-specific parameters</param>
    /// <param name="layer">Target layer definition</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Unified query or error result</returns>
    Task<QueryAdapterResult> ConvertAsync(TProtocolParams parameters, LayerDefinition layer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the protocol name for this adapter.
    /// </summary>
    string ProtocolName { get; }

    /// <summary>
    /// Gets the default limits for this protocol.
    /// </summary>
    ProtocolLimits DefaultLimits { get; }
}

/// <summary>
/// Result of parameter conversion to unified query.
/// </summary>
public sealed record QueryAdapterResult
{
    /// <summary>
    /// Whether the conversion succeeded.
    /// </summary>
    public required bool IsSuccess { get; init; }

    /// <summary>
    /// Unified query if conversion succeeded.
    /// </summary>
    public UnifiedQuery? Query { get; init; }

    /// <summary>
    /// Error message if conversion failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Protocol-specific metadata that may be needed for response formatting.
    /// </summary>
    public IDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Creates a successful conversion result.
    /// </summary>
    /// <param name="query">Unified query</param>
    /// <param name="metadata">Optional protocol-specific metadata</param>
    /// <returns>Successful result</returns>
    public static QueryAdapterResult Success(UnifiedQuery query, IDictionary<string, object>? metadata = null)
        => new() { IsSuccess = true, Query = query, Metadata = metadata };

    /// <summary>
    /// Creates a failed conversion result.
    /// </summary>
    /// <param name="errorMessage">Error message</param>
    /// <returns>Failed result</returns>
    public static QueryAdapterResult Failure(string errorMessage)
        => new() { IsSuccess = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Protocol-specific limits and constraints.
/// </summary>
public sealed record ProtocolLimits
{
    /// <summary>
    /// Maximum number of results per query.
    /// </summary>
    public int MaxResultCount { get; init; } = 10000;

    /// <summary>
    /// Default number of results if not specified.
    /// </summary>
    public int DefaultResultCount { get; init; } = 100;

    /// <summary>
    /// Maximum offset for pagination.
    /// </summary>
    public int MaxOffset { get; init; } = 100000;

    /// <summary>
    /// Maximum number of fields that can be selected.
    /// </summary>
    public int MaxFieldCount { get; init; } = 100;

    /// <summary>
    /// Maximum depth for nested filters.
    /// </summary>
    public int MaxFilterDepth { get; init; } = 10;

    /// <summary>
    /// Whether spatial queries are supported.
    /// </summary>
    public bool SupportsSpatialQueries { get; init; } = true;

    /// <summary>
    /// Whether temporal queries are supported.
    /// </summary>
    public bool SupportsTemporalQueries { get; init; } = true;

    /// <summary>
    /// Whether aggregation is supported.
    /// </summary>
    public bool SupportsAggregation { get; init; }

    /// <summary>
    /// Standard limits for OGC API Features.
    /// </summary>
    public static ProtocolLimits OgcFeatures => new()
    {
        MaxResultCount = 10000,
        DefaultResultCount = 10,
        MaxOffset = 100000,
        MaxFieldCount = 100,
        MaxFilterDepth = 10,
        SupportsSpatialQueries = true,
        SupportsTemporalQueries = true,
        SupportsAggregation = false
    };

    /// <summary>
    /// Standard limits for GeoServices.
    /// </summary>
    public static ProtocolLimits GeoServices => new()
    {
        MaxResultCount = 2000,
        DefaultResultCount = 1000,
        MaxOffset = 50000,
        MaxFieldCount = 50,
        MaxFilterDepth = 15,
        SupportsSpatialQueries = true,
        SupportsTemporalQueries = true,
        SupportsAggregation = true
    };

    /// <summary>
    /// Standard limits for WFS 2.0.
    /// </summary>
    public static ProtocolLimits Wfs20 => new()
    {
        MaxResultCount = 5000,
        DefaultResultCount = 100,
        MaxOffset = 100000,
        MaxFieldCount = 100,
        MaxFilterDepth = 20,
        SupportsSpatialQueries = true,
        SupportsTemporalQueries = true,
        SupportsAggregation = false
    };

    /// <summary>
    /// Standard limits for OData.
    /// </summary>
    public static ProtocolLimits OData => new()
    {
        MaxResultCount = 1000,
        DefaultResultCount = 100,
        MaxOffset = 10000,
        MaxFieldCount = 50,
        MaxFilterDepth = 10,
        SupportsSpatialQueries = false,
        SupportsTemporalQueries = true,
        SupportsAggregation = true
    };
}
