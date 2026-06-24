// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Federation;
using Honua.Core.Features.Federation.Abstractions;
using Honua.Core.Features.Federation.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.Core.Tests.Features.Federation;

/// <summary>
/// Unit tests for the pure federation query planner (issue #341). The planner runs offline,
/// so these tests exercise the push-down / local-refinement decisions with no remote source.
/// </summary>
public sealed class FederationQueryPlannerTests
{
    private readonly IFederationQueryPlanner _planner = BuildPlanner();

    private static IFederationQueryPlanner BuildPlanner()
    {
        var services = new ServiceCollection();
        services.AddFederationCore();
        return services.BuildServiceProvider().GetRequiredService<IFederationQueryPlanner>();
    }

    private static FederatedSourceDescriptor Source(FederatedSourceKind kind) => new()
    {
        Id = $"src-{kind}",
        DisplayName = kind.ToString(),
        Kind = kind,
        Endpoint = new System.Uri("https://remote.example.com/"),
        RemoteLayer = "0",
        RequestTimeout = System.TimeSpan.FromSeconds(30),
    };

    [UnitTest]
    public void AddFederationCore_RegistersPlanner()
    {
        Assert.NotNull(_planner);
    }

    [UnitTest]
    public void ResolveCapabilities_HonuaGrpc_PushesEverythingDown()
    {
        var caps = _planner.ResolveCapabilities(FederatedSourceKind.HonuaGrpc);

        Assert.True(caps.SupportsAttributeFilterPushdown);
        Assert.True(caps.SupportsSpatialFilterPushdown);
        Assert.True(caps.SupportsExactSpatialRelationshipPushdown);
        Assert.True(caps.SupportsTemporalFilterPushdown);
        Assert.True(caps.SupportsOrderByPushdown);
        Assert.True(caps.SupportsPagingPushdown);
    }

    [UnitTest]
    public void ResolveCapabilities_EsriRest_RefinesExactSpatialAndTemporalLocally()
    {
        var caps = _planner.ResolveCapabilities(FederatedSourceKind.EsriRest);

        Assert.True(caps.SupportsAttributeFilterPushdown);
        Assert.True(caps.SupportsSpatialFilterPushdown);
        Assert.False(caps.SupportsExactSpatialRelationshipPushdown);
        Assert.False(caps.SupportsTemporalFilterPushdown);
        Assert.True(caps.SupportsOrderByPushdown);
    }

    [UnitTest]
    public void ResolveCapabilities_OgcWfs_DoesNotPushOrderByDown()
    {
        var caps = _planner.ResolveCapabilities(FederatedSourceKind.OgcWfs);

        Assert.True(caps.SupportsAttributeFilterPushdown);
        Assert.True(caps.SupportsSpatialFilterPushdown);
        Assert.False(caps.SupportsOrderByPushdown);
    }

    [UnitTest]
    public void Plan_HonuaGrpc_AttributeFilter_PushesDownWithNoLocalRefinement()
    {
        var query = FeatureQuery.WithWhere("population > 1000");

        var plan = _planner.Plan(Source(FederatedSourceKind.HonuaGrpc), in query, joinsLocalLayer: false);

        var decision = Assert.Single(plan.Decisions, d => d.Predicate == FederationPredicateKind.AttributeFilter);
        Assert.True(decision.PushedDown);
        Assert.False(plan.RequiresLocalRefinement);
        Assert.False(plan.RequiresLocalJoin);
    }

    [UnitTest]
    public void Plan_EsriRest_ExactSpatialRelationship_RefinedLocally()
    {
        var query = FeatureQuery.WithSpatialFilter(new SpatialFilter
        {
            Geometry = System.Array.Empty<byte>(),
            SpatialRelationship = SpatialRelationship.Within,
        });

        var plan = _planner.Plan(Source(FederatedSourceKind.EsriRest), in query, joinsLocalLayer: false);

        var spatial = Assert.Single(plan.Decisions, d => d.Predicate == FederationPredicateKind.SpatialFilter);
        Assert.False(spatial.PushedDown);
        Assert.True(plan.RequiresLocalRefinement);
    }

    [UnitTest]
    public void Plan_EsriRest_EnvelopeIntersects_PushesDown()
    {
        var query = FeatureQuery.WithSpatialFilter(new SpatialFilter
        {
            Geometry = System.Array.Empty<byte>(),
            SpatialRelationship = SpatialRelationship.EnvelopeIntersects,
            IsSimpleEnvelope = true,
        });

        var plan = _planner.Plan(Source(FederatedSourceKind.EsriRest), in query, joinsLocalLayer: false);

        var spatial = Assert.Single(plan.Decisions, d => d.Predicate == FederationPredicateKind.SpatialFilter);
        Assert.True(spatial.PushedDown);
    }

    [UnitTest]
    public void Plan_EsriRest_TemporalFilter_RefinedLocally()
    {
        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "observed_at",
                PropertyType = TemporalPropertyType.DateTime,
                Start = System.DateTimeOffset.UnixEpoch,
            },
        };

        var plan = _planner.Plan(Source(FederatedSourceKind.EsriRest), in query, joinsLocalLayer: false);

        var temporal = Assert.Single(plan.Decisions, d => d.Predicate == FederationPredicateKind.TemporalFilter);
        Assert.False(temporal.PushedDown);
    }

    [UnitTest]
    public void Plan_OgcWfs_OrderByLocal_ForcesPagingLocal()
    {
        var query = new FeatureQuery
        {
            OrderBy = ImmutableArray.Create(OrderByClause.Asc("name")),
            Limit = 50,
            Offset = 100,
        };

        var plan = _planner.Plan(Source(FederatedSourceKind.OgcWfs), in query, joinsLocalLayer: false);

        var orderBy = Assert.Single(plan.Decisions, d => d.Predicate == FederationPredicateKind.OrderBy);
        var paging = Assert.Single(plan.Decisions, d => d.Predicate == FederationPredicateKind.Paging);
        Assert.False(orderBy.PushedDown);
        Assert.False(paging.PushedDown);
    }

    [UnitTest]
    public void Plan_HonuaGrpc_OrderByThenPaging_BothPushDown()
    {
        var query = new FeatureQuery
        {
            OrderBy = ImmutableArray.Create(OrderByClause.Asc("name")),
            Limit = 50,
        };

        var plan = _planner.Plan(Source(FederatedSourceKind.HonuaGrpc), in query, joinsLocalLayer: false);

        Assert.True(Assert.Single(plan.Decisions, d => d.Predicate == FederationPredicateKind.OrderBy).PushedDown);
        Assert.True(Assert.Single(plan.Decisions, d => d.Predicate == FederationPredicateKind.Paging).PushedDown);
    }

    [UnitTest]
    public void Plan_NoPredicates_ProducesEmptyDecisionsButCanRequireJoin()
    {
        var query = new FeatureQuery();

        var plan = _planner.Plan(Source(FederatedSourceKind.HonuaGrpc), in query, joinsLocalLayer: true);

        Assert.Empty(plan.Decisions);
        Assert.False(plan.RequiresLocalRefinement);
        Assert.True(plan.RequiresLocalJoin);
    }

    [UnitTest]
    public void Plan_NullSource_Throws()
    {
        var query = new FeatureQuery();

        Assert.Throws<System.ArgumentNullException>(() => _planner.Plan(null!, in query, joinsLocalLayer: false));
    }
}
