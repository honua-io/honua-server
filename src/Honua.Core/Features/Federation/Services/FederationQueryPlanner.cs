// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Federation.Abstractions;
using Honua.Core.Features.Federation.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Federation.Services;

/// <summary>
/// Pure, deterministic federation query planner. Given a canonical <see cref="FeatureQuery"/>
/// and a federated source, it decides which predicates push down to the remote source and
/// which are refined locally, following the "push filters to remote, join locally" strategy
/// in issue #341. The planner performs no I/O, so it is safe to run offline and is exercised
/// by unit tests with no live remote source.
/// </summary>
internal sealed class FederationQueryPlanner : IFederationQueryPlanner
{
    public FederatedSourceCapabilities ResolveCapabilities(FederatedSourceKind kind) => kind switch
    {
        // Honua-to-Honua over gRPC shares the canonical filter model end to end, so every
        // predicate family pushes down.
        FederatedSourceKind.HonuaGrpc => new FederatedSourceCapabilities
        {
            SupportsAttributeFilterPushdown = true,
            SupportsSpatialFilterPushdown = true,
            SupportsExactSpatialRelationshipPushdown = true,
            SupportsTemporalFilterPushdown = true,
            SupportsOrderByPushdown = true,
            SupportsPagingPushdown = true,
        },

        // Esri ArcGIS REST query accepts a SQL-like WHERE, an envelope, ORDER BY, and
        // result paging, but only envelope-grade spatial relationships and no first-class
        // temporal-interval predicate, so exact spatial relationships and temporal filters
        // are refined locally.
        FederatedSourceKind.EsriRest => new FederatedSourceCapabilities
        {
            SupportsAttributeFilterPushdown = true,
            SupportsSpatialFilterPushdown = true,
            SupportsExactSpatialRelationshipPushdown = false,
            SupportsTemporalFilterPushdown = false,
            SupportsOrderByPushdown = true,
            SupportsPagingPushdown = true,
        },

        // OGC API - Features / WFS accept CQL2 attribute predicates and a bbox, but the
        // baseline conformance classes do not guarantee exact spatial relationships,
        // temporal filtering, or ordering, so those are refined locally.
        FederatedSourceKind.OgcWfs => new FederatedSourceCapabilities
        {
            SupportsAttributeFilterPushdown = true,
            SupportsSpatialFilterPushdown = true,
            SupportsExactSpatialRelationshipPushdown = false,
            SupportsTemporalFilterPushdown = false,
            SupportsOrderByPushdown = false,
            SupportsPagingPushdown = true,
        },

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown federated source kind."),
    };

    public FederationQueryPlan Plan(FederatedSourceDescriptor source, in FeatureQuery query, bool joinsLocalLayer)
    {
        ArgumentNullException.ThrowIfNull(source);

        var capabilities = ResolveCapabilities(source.Kind);
        var decisions = ImmutableArray.CreateBuilder<FederationPredicateDecision>();

        if (HasAttributeFilter(query))
        {
            decisions.Add(Decide(
                FederationPredicateKind.AttributeFilter,
                capabilities.SupportsAttributeFilterPushdown,
                "attribute (WHERE / object-id) predicate"));
        }

        if (query.SpatialFilter is { } spatial)
        {
            var exact = RequiresExactSpatialRelationship(spatial);
            var pushed = capabilities.SupportsSpatialFilterPushdown &&
                (!exact || capabilities.SupportsExactSpatialRelationshipPushdown);

            var reason = !capabilities.SupportsSpatialFilterPushdown
                ? "spatial predicate unsupported remotely"
                : exact && !capabilities.SupportsExactSpatialRelationshipPushdown
                    ? "exact spatial relationship refined locally over a pushed-down envelope superset"
                    : "spatial (envelope) predicate";

            decisions.Add(new FederationPredicateDecision(FederationPredicateKind.SpatialFilter, pushed, reason));
        }

        if (query.TemporalFilter is not null)
        {
            decisions.Add(Decide(
                FederationPredicateKind.TemporalFilter,
                capabilities.SupportsTemporalFilterPushdown,
                "temporal interval predicate"));
        }

        if (query.OrderBy is { IsDefaultOrEmpty: false })
        {
            decisions.Add(Decide(
                FederationPredicateKind.OrderBy,
                capabilities.SupportsOrderByPushdown,
                "result ordering"));
        }

        if (query.Limit is not null || query.Offset is not null)
        {
            // Paging can only push down when the full sort is also remote; otherwise the
            // remote page would be computed over an unordered (or locally re-sorted) set
            // and would not match the federated result page.
            var orderPushedDown = !(query.OrderBy is { IsDefaultOrEmpty: false }) || capabilities.SupportsOrderByPushdown;
            var pushed = capabilities.SupportsPagingPushdown && orderPushedDown;
            var reason = !capabilities.SupportsPagingPushdown
                ? "paging unsupported remotely; over-fetch and page locally"
                : orderPushedDown
                    ? "offset/limit paging"
                    : "ordering refined locally, so paging is applied locally after the sort";

            decisions.Add(new FederationPredicateDecision(FederationPredicateKind.Paging, pushed, reason));
        }

        return new FederationQueryPlan
        {
            SourceId = source.Id,
            Capabilities = capabilities,
            Decisions = decisions.ToImmutable(),
            RequiresLocalJoin = joinsLocalLayer,
        };
    }

    private static FederationPredicateDecision Decide(FederationPredicateKind kind, bool supported, string label) =>
        new(kind, supported, supported ? $"{label} pushed to remote source" : $"{label} refined locally");

    private static bool HasAttributeFilter(in FeatureQuery query) =>
        !string.IsNullOrWhiteSpace(query.Where) ||
        query.SqlFilter is not null ||
        (query.ObjectIds is { IsDefaultOrEmpty: false });

    private static bool RequiresExactSpatialRelationship(in SpatialFilter spatial) =>
        spatial.SpatialRelationship switch
        {
            SpatialRelationship.Intersects => false,
            SpatialRelationship.EnvelopeIntersects => false,
            _ => true,
        };
}
