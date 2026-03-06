// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Immutable;
using Geospatial.V1;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Transport.Converters;

/// <summary>
/// Converter for bidirectional conversion between Honua domain models and geospatial gRPC protocol messages.
/// Handles feature queries, results, and related spatial operations.
/// </summary>
public static class FeatureConverter
{
    /// <summary>
    /// Converts a Honua domain FeatureQuery to a gRPC QueryFeaturesRequest.
    /// </summary>
    /// <param name="query">The domain feature query</param>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <returns>gRPC request message</returns>
    public static QueryFeaturesRequest ToGrpcRequest(FeatureQuery query, string serviceId, int layerId)
    {
        var request = new QueryFeaturesRequest
        {
            ServiceId = serviceId,
            LayerId = layerId,
            ReturnGeometry = true, // Domain query doesn't specify geometry return preference, default to true
            ReturnDistinct = query.Distinct
        };

        if (!string.IsNullOrEmpty(query.Where))
        {
            request.Where = query.Where;
        }

        if (query.ObjectIds?.Length > 0)
        {
            request.ObjectIds.AddRange(query.ObjectIds.Value);
        }

        if (query.OutFields?.Length > 0)
        {
            request.OutFields.AddRange(query.OutFields.Value);
        }

        if (query.Offset.HasValue)
        {
            request.ResultOffset = query.Offset.Value;
        }

        if (query.Limit.HasValue)
        {
            request.ResultRecordCount = query.Limit.Value;
        }

        if (query.OrderBy?.Length > 0)
        {
            // Convert OrderByClause array to string format
            var orderByParts = query.OrderBy.Value.Select(clause =>
                $"{clause.Field} {(clause.Ascending ? "ASC" : "DESC")}");
            request.OrderBy = string.Join(", ", orderByParts);
        }

        if (query.SpatialFilter != null)
        {
            request.SpatialFilter = SpatialFilterConverter.ToGrpc(query.SpatialFilter.Value);
        }

        if (query.OutStatistics?.Length > 0)
        {
            foreach (var stat in query.OutStatistics.Value)
            {
                request.OutStatistics.Add(StatisticDefinitionConverter.ToGrpc(stat));
            }
        }

        if (query.GroupByFields?.Length > 0)
        {
            request.GroupBy.AddRange(query.GroupByFields.Value);
        }

        return request;
    }

    /// <summary>
    /// Converts a gRPC QueryFeaturesRequest to a Honua domain FeatureQuery.
    /// </summary>
    /// <param name="request">The gRPC request message</param>
    /// <returns>Domain feature query</returns>
    public static FeatureQuery FromGrpcRequest(QueryFeaturesRequest request)
    {
        // Parse OrderBy string into OrderByClause array
        ImmutableArray<OrderByClause>? orderBy = null;
        if (!string.IsNullOrEmpty(request.OrderBy))
        {
            var clauses = request.OrderBy.Split(',')
                .Select(clause => clause.Trim())
                .Where(clause => !string.IsNullOrEmpty(clause))
                .Select(clause =>
                {
                    var parts = clause.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var fieldName = parts[0];
                    var ascending = parts.Length < 2 ||
                        !string.Equals(parts[1], "DESC", StringComparison.OrdinalIgnoreCase);
                    return new OrderByClause(fieldName, ascending);
                });
            orderBy = clauses.ToImmutableArray();
        }

        // Build statistics list
        ImmutableArray<Features.FeatureStore.Domain.StatisticDefinition>? statistics = null;
        if (request.OutStatistics.Count > 0)
        {
            statistics = request.OutStatistics
                .Select(grpcStat => StatisticDefinitionConverter.FromGrpc(grpcStat))
                .ToImmutableArray();
        }

        return new FeatureQuery
        {
            Where = string.IsNullOrEmpty(request.Where) ? null : request.Where,
            ObjectIds = request.ObjectIds.Count > 0 ? request.ObjectIds.ToImmutableArray() : null,
            OutFields = request.OutFields.Count > 0 ? request.OutFields.ToImmutableArray() : null,
            SpatialFilter = request.SpatialFilter != null ? SpatialFilterConverter.FromGrpc(request.SpatialFilter) : null,
            Offset = request.ResultOffset > 0 ? request.ResultOffset : null,
            Limit = request.ResultRecordCount > 0 ? request.ResultRecordCount : null,
            OrderBy = orderBy,
            Distinct = request.ReturnDistinct,
            OutStatistics = statistics,
            GroupByFields = request.GroupBy.Count > 0 ? request.GroupBy.ToImmutableArray() : null
        };
    }

    /// <summary>
    /// Converts a gRPC QueryFeaturesResponse to a domain QueryResult&lt;Feature&gt;.
    /// </summary>
    /// <param name="response">The gRPC response message</param>
    /// <returns>Domain query result</returns>
    public static Features.FeatureStore.Domain.QueryResult<Features.FeatureStore.Domain.Feature> FromGrpcResponse(QueryFeaturesResponse response)
    {
        var features = new List<Features.FeatureStore.Domain.Feature>();

        foreach (var grpcFeature in response.Features)
        {
            byte[]? geometryWkb = null;
            if (grpcFeature.Geometry != null)
            {
                // Convert gRPC geometry to NTS geometry, then to WKB
                var ntsGeometry = GeometryConverter.FromGrpc(grpcFeature.Geometry);
                geometryWkb = GeometryConverter.ToWkb(ntsGeometry);
            }

            var feature = Features.FeatureStore.Domain.Feature.Create(
                id: grpcFeature.Id,
                geometry: geometryWkb,
                attributes: AttributeConverter.FromGrpc(grpcFeature.Attributes).ToImmutableDictionary()
            );

            features.Add(feature);
        }

        return Features.FeatureStore.Domain.QueryResult<Features.FeatureStore.Domain.Feature>.Create(
            totalCount: response.Count,
            items: features.ToImmutableArray(),
            hasMoreResults: response.ExceededTransferLimit
        );
    }

    /// <summary>
    /// Converts domain Features to gRPC Feature messages for editing operations.
    /// </summary>
    /// <param name="domainFeatures">Domain features to convert</param>
    /// <returns>gRPC feature messages</returns>
    public static IEnumerable<Geospatial.V1.Feature> ToGrpcFeatures(IEnumerable<Features.FeatureStore.Domain.Feature> domainFeatures)
    {
        foreach (var feature in domainFeatures)
        {
            var grpcFeature = new Geospatial.V1.Feature
            {
                Id = feature.Id
            };

            foreach (var attribute in feature.Attributes)
            {
                grpcFeature.Attributes[attribute.Key] = AttributeConverter.ToGrpc(attribute.Value);
            }

            if (feature.Geometry != null)
            {
                // Convert WKB to NTS geometry, then to gRPC geometry
                var ntsGeometry = GeometryConverter.FromWkb(feature.Geometry);
                grpcFeature.Geometry = GeometryConverter.ToGrpc(ntsGeometry);
            }

            yield return grpcFeature;
        }
    }
}

