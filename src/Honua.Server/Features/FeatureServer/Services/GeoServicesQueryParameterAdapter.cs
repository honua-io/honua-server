// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Query;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Validation;

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Prepared GeoServices query inputs ready for protocol-adapter conversion.
/// </summary>
internal readonly record struct GeoServicesQueryRequest
{
    public required QueryParameters Parameters { get; init; }

    public GeoServicesGeometry? ParsedGeometry { get; init; }

    public int? InputSrid { get; init; }

    public int? OutputSrid { get; init; }

    public required QueryLimits QueryLimits { get; init; }

    public SqlFragment? SqlFilter { get; init; }
}

/// <summary>
/// Converts validated GeoServices query inputs into the shared unified query model.
/// </summary>
internal sealed class GeoServicesQueryParameterAdapter(
    ILogger<GeoServicesQueryParameterAdapter> logger) : IQueryParameterAdapter<GeoServicesQueryRequest>
{
    private readonly ILogger<GeoServicesQueryParameterAdapter> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public string ProtocolName => "GeoServices";

    public ProtocolLimits DefaultLimits => ProtocolLimits.GeoServices;

    public Task<QueryAdapterResult> ConvertAsync(
        GeoServicesQueryRequest request,
        LayerDefinition layer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var queryParams = request.Parameters;
            var hasObjectIds = queryParams.ObjectIds is { Length: > 0 };
            var outFields = ResolveOutFields(queryParams);
            var spatialFilter = ResolveSpatialFilter(queryParams, request.ParsedGeometry, request.InputSrid);
            var orderBy = OrderByParsing.ParseFeatureServerOrderBy(
                queryParams.OrderByFields,
                layer,
                FeatureServerOrderByFields.AllowedCoreOrderByFields);

            QueryFilter? filter = null;
            if (request.SqlFilter != null)
            {
                filter = QueryFilter.FromSql(
                    request.SqlFilter,
                    new FilterSource(queryParams.Where ?? queryParams.Time ?? string.Empty, FilterLanguage.ArcGisSql, ProtocolName));
            }

            QueryAggregation? aggregation = queryParams.ReturnDistinctValues
                ? QueryAggregation.Create(distinct: true)
                : null;

            var metadata = new Dictionary<string, object>
            {
                ["f"] = queryParams.F ?? "json",
                ["returnGeometry"] = queryParams.ReturnGeometry,
                ["returnCountOnly"] = queryParams.ReturnCountOnly,
                ["returnExtentOnly"] = queryParams.ReturnExtentOnly,
                ["returnIdsOnly"] = queryParams.ReturnIdsOnly
            };

            if (queryParams.GeometryPrecision.HasValue)
            {
                metadata["geometryPrecision"] = queryParams.GeometryPrecision.Value;
            }

            if (queryParams.MaxAllowableOffset.HasValue)
            {
                metadata["maxAllowableOffset"] = queryParams.MaxAllowableOffset.Value;
            }

            var unifiedQuery = new UnifiedQuery
            {
                Filter = filter,
                SpatialFilter = spatialFilter,
                ObjectIds = hasObjectIds ? queryParams.ObjectIds?.ToImmutableArray() : null,
                OutFields = outFields,
                Offset = queryParams.ResultOffset,
                Limit = hasObjectIds
                    ? queryParams.ResultRecordCount ?? queryParams.ObjectIds?.Length
                    : queryParams.ResultRecordCount ?? request.QueryLimits.DefaultRecordCount,
                OrderBy = orderBy,
                OutputCrs = request.OutputSrid.HasValue
                    ? QueryCrs.Create(request.OutputSrid.Value)
                    : null,
                Aggregation = aggregation,
                Hints = QueryHints.Create(
                    preferStreaming: (queryParams.ResultRecordCount ?? request.QueryLimits.DefaultRecordCount) > DefaultLimits.DefaultResultCount,
                    enableCaching: true,
                    requireExactCount: queryParams.ReturnCountOnly || queryParams.ReturnExtentOnly || queryParams.ReturnIdsOnly)
            };

            return Task.FromResult(QueryAdapterResult.Success(unifiedQuery, metadata));
        }
        catch (Exception ex)
        {
            GeoServicesPreparedAdaptersLog.QueryParameterConversionFailed(_logger, ex);
            return Task.FromResult(QueryAdapterResult.Failure("Invalid query parameters."));
        }
    }

    private static ImmutableArray<string>? ResolveOutFields(QueryParameters queryParams)
    {
        if (string.IsNullOrEmpty(queryParams.OutFields) ||
            string.Equals(queryParams.OutFields, "*", StringComparison.Ordinal))
        {
            return null;
        }

        return queryParams.OutFields.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(static field => field.Trim())
            .ToImmutableArray();
    }

    private static SpatialFilter? ResolveSpatialFilter(
        QueryParameters queryParams,
        GeoServicesGeometry? parsedGeometry,
        int? inputSrid)
    {
        if (parsedGeometry == null && !queryParams.NearestCount.HasValue)
        {
            return null;
        }

        if (queryParams.NearestCount.HasValue && parsedGeometry == null)
        {
            throw new InvalidOperationException("Geometry is required for nearest neighbor queries.");
        }

        return FeatureServerSpatialFilterBuilder.BuildSpatialFilter(
            queryParams,
            parsedGeometry!,
            inputSrid);
    }
}
