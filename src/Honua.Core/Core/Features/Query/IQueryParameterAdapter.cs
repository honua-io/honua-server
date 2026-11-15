// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Query;

public interface IQueryParameterAdapter<in TProtocolParams>
{
    Task<QueryAdapterResult> ConvertAsync(TProtocolParams parameters, MetadataV2Object resource, CancellationToken cancellationToken = default);

    string ProtocolName { get; }

    ProtocolLimits DefaultLimits { get; }
}

public sealed record QueryAdapterResult
{
    public required bool IsSuccess { get; init; }

    public UnifiedQuery? Query { get; init; }

    public string? ErrorMessage { get; init; }

    public IDictionary<string, object>? Metadata { get; init; }

    public static QueryAdapterResult Success(UnifiedQuery query, IDictionary<string, object>? metadata = null)
        => new() { IsSuccess = true, Query = query, Metadata = metadata };

    public static QueryAdapterResult Failure(string errorMessage)
        => new() { IsSuccess = false, ErrorMessage = errorMessage };
}

public sealed record ProtocolLimits
{
    public int MaxResultCount { get; init; } = 10000;

    public int DefaultResultCount { get; init; } = 100;

    public int MaxOffset { get; init; } = 100000;

    public int MaxFieldCount { get; init; } = 100;

    public int MaxFilterDepth { get; init; } = 10;

    public bool SupportsSpatialQueries { get; init; } = true;

    public bool SupportsTemporalQueries { get; init; } = true;

    public bool SupportsAggregation { get; init; }

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
