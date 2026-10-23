// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.SpatialAnalytics;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.ObjectPool;
using NSubstitute;
using static Honua.Db.Postgres.Tests.Features.SpatialAnalytics.SpatialAnalyticsReadPolicyTestData;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Db.Postgres.Tests.Features.SpatialAnalytics;

/// <summary>
/// The spatial analytics reader must apply the same read policy as a direct feature
/// query against the same layer: the row policy narrows the rows every operation
/// aggregates, and masked fields are neither returned nor usable as operands. Spatial
/// join applies each layer's own policy to its own side.
/// </summary>
public sealed class PostgresSpatialAnalyticsReaderReadPolicyTests
{
    [UnitTheory]
    [InlineData("secret")]
    [InlineData("SeCrEt")]
    public async Task ManagedFeatureStore_TemporalExtentOfMaskedField_IsAbsentWithoutDatabaseRead(string field)
    {
        var harness = new Harness().WithMaskedFields(TargetLayerId, "SECRET");

        var extent = await harness.FeatureStore.GetTemporalExtentAsync(
            TargetLayerId, field, TemporalPropertyType.DateTime);

        extent.Should().BeNull();
        harness.TemporalReadCount.Should().Be(0);
    }

    [UnitTest]
    public async Task QueryClustersAsync_WithRowPolicy_FiltersSourceRows()
    {
        var harness = new Harness().WithRowPolicy(TargetLayerId, "attributes->>'region' = @p0", "west");

        await harness.Reader.QueryClustersAsync(TargetLayerId, new FeatureQuery(), KMeans());

        harness.Sql.Should().Contain("attributes->>'region' = $");
        harness.Parameters.Should().Contain("west");
        harness.ExecutedQuery.EnforcedSqlFilter.Should().NotBeNull();
    }

    [UnitTest]
    public async Task QueryBufferAggregateAsync_WithRowPolicy_FiltersSourceRows()
    {
        var harness = new Harness().WithRowPolicy(TargetLayerId, "attributes->>'region' = @p0", "west");

        await harness.Reader.QueryBufferAggregateAsync(TargetLayerId, new FeatureQuery(), Buffer());

        harness.Sql.Should().Contain("attributes->>'region' = $");
        harness.Parameters.Should().Contain("west");
    }

    [UnitTest]
    public async Task QueryDensityAsync_WithRowPolicy_FiltersSourceRows()
    {
        var harness = new Harness().WithRowPolicy(TargetLayerId, "attributes->>'region' = @p0", "west");

        await harness.Reader.QueryDensityAsync(TargetLayerId, new FeatureQuery(), Density());

        harness.Sql.Should().Contain("attributes->>'region' = $");
        harness.Parameters.Should().Contain("west");
    }

    [UnitTest]
    public async Task QuerySpatialJoinAsync_WithRowPolicies_FiltersEachLayerWithItsOwnPolicy()
    {
        var harness = new Harness()
            .WithRowPolicy(TargetLayerId, "attributes->>'region' = @p0", "west")
            .WithRowPolicy(JoinLayerId, "attributes->>'owner' = @p0", "team-a");

        await harness.Reader.QuerySpatialJoinAsync(TargetLayerId, new FeatureQuery(), Join());

        var sql = harness.Sql;
        sql.Should().Contain("attributes->>'region' = $");
        sql.Should().Contain("attributes->>'owner' = $");
        harness.Parameters.Should().Contain("west").And.Contain("team-a");

        // The target policy lives inside the target CTE; the join policy inside the
        // join-side derived table, so neither can bind to the other layer's rows.
        var joinSide = sql[sql.IndexOf("LEFT JOIN (SELECT", StringComparison.Ordinal)..];
        joinSide.Should().Contain("attributes->>'owner' = $");
        joinSide.Should().NotContain("attributes->>'region' = $");
    }

    [UnitTest]
    public async Task QueryClustersAsync_PerFeatureModeWithMaskedField_RemovesItFromAttributes()
    {
        var harness = new Harness().WithMaskedFields(TargetLayerId, "secret");

        await harness.Reader.QueryClustersAsync(TargetLayerId, new FeatureQuery(), KMeans());

        harness.Sql.Should().Contain("jsonb_each(attributes)")
            .And.Contain("COALESCE(jsonb_object_agg(masked_attr.key, masked_attr.value), '{}'::jsonb)")
            .And.Contain("WHERE NOT (UPPER(masked_attr.key) = ANY($")
            .And.Contain("::text[])");
        harness.Parameters.OfType<string[]>().Should().ContainSingle()
            .Which.Should().Equal("SECRET");
    }

    [UnitTest]
    public async Task QuerySpatialJoinAsync_WithMaskedTargetField_RemovesItFromTargetAttributes()
    {
        var harness = new Harness().WithMaskedFields(TargetLayerId, "secret");

        await harness.Reader.QuerySpatialJoinAsync(TargetLayerId, new FeatureQuery(), Join());

        harness.Sql.Should().Contain("jsonb_each(attributes)")
            .And.Contain("COALESCE(jsonb_object_agg(masked_attr.key, masked_attr.value), '{}'::jsonb)")
            .And.Contain("WHERE NOT (UPPER(masked_attr.key) = ANY($")
            .And.Contain("::text[])");
        harness.Parameters.OfType<string[]>().Should().ContainSingle()
            .Which.Should().Equal("SECRET");
    }

    [UnitTest]
    public async Task QuerySpatialJoinAsync_CarryFieldMaskedOnJoinLayer_IsRejectedBeforeDatabaseAccess()
    {
        var harness = new Harness().WithMaskedFields(JoinLayerId, "secret");
        var join = Join() with { CarryFields = ["name", "Secret"] };

        var act = () => harness.Reader.QuerySpatialJoinAsync(TargetLayerId, new FeatureQuery(), join);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("Secret");
        harness.AssertNoDatabaseAccess();
    }

    [UnitTest]
    public async Task QuerySpatialJoinAsync_JoinStatisticOnMaskedField_IsRejectedBeforeDatabaseAccess()
    {
        var harness = new Harness().WithMaskedFields(JoinLayerId, "secret");
        var join = Join() with { OutStatistics = [Sum("secret")] };

        var act = () => harness.Reader.QuerySpatialJoinAsync(TargetLayerId, new FeatureQuery(), join);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("secret");
        harness.AssertNoDatabaseAccess();
    }

    [UnitTest]
    public async Task QuerySpatialJoinAsync_CarryFieldMaskedOnlyOnTargetLayer_IsAllowed()
    {
        // Masks are per layer: a field masked on the target says nothing about a
        // same-named field on the join layer.
        var harness = new Harness().WithMaskedFields(TargetLayerId, "secret");
        var join = Join() with { CarryFields = ["secret"] };

        await harness.Reader.QuerySpatialJoinAsync(TargetLayerId, new FeatureQuery(), join);

        harness.Sql.Should().Contain("array_agg(");
    }

    [UnitTest]
    public async Task QueryClustersAsync_StatisticOnMaskedField_IsRejectedBeforeDatabaseAccess()
    {
        var harness = new Harness().WithMaskedFields(TargetLayerId, "secret");
        var cluster = KMeans() with { ReturnHullPerCluster = true, OutStatistics = [Sum("secret")] };

        var act = () => harness.Reader.QueryClustersAsync(TargetLayerId, new FeatureQuery(), cluster);

        await act.Should().ThrowAsync<ArgumentException>();
        harness.AssertNoDatabaseAccess();
    }

    [UnitTest]
    public async Task QueryClustersAsync_WhereOnMaskedField_IsRejectedBeforeDatabaseAccess()
    {
        var harness = new Harness().WithMaskedFields(TargetLayerId, "secret");
        var query = new FeatureQuery { Where = "secret = 'x'" };

        var act = () => harness.Reader.QueryClustersAsync(TargetLayerId, query, KMeans());

        await act.Should().ThrowAsync<ArgumentException>();
        harness.AssertNoDatabaseAccess();
    }

    [UnitTest]
    public async Task QueryBufferAggregateAsync_GroupByMaskedField_IsRejectedBeforeDatabaseAccess()
    {
        var harness = new Harness().WithMaskedFields(TargetLayerId, "secret");
        var buffer = Buffer() with { GroupByFields = ["secret"] };

        var act = () => harness.Reader.QueryBufferAggregateAsync(TargetLayerId, new FeatureQuery(), buffer);

        await act.Should().ThrowAsync<ArgumentException>();
        harness.AssertNoDatabaseAccess();
    }

    [UnitTest]
    public async Task QueryBufferAggregateAsync_StatisticOnMaskedField_IsRejectedBeforeDatabaseAccess()
    {
        var harness = new Harness().WithMaskedFields(TargetLayerId, "secret");
        var buffer = Buffer() with { OutStatistics = [Sum("secret")] };

        var act = () => harness.Reader.QueryBufferAggregateAsync(TargetLayerId, new FeatureQuery(), buffer);

        await act.Should().ThrowAsync<ArgumentException>();
        harness.AssertNoDatabaseAccess();
    }

    [UnitTest]
    public async Task QueryDensityAsync_WeightOnMaskedField_IsRejectedBeforeDatabaseAccess()
    {
        var harness = new Harness().WithMaskedFields(TargetLayerId, "secret");
        var density = Density() with { WeightField = "secret" };

        var act = () => harness.Reader.QueryDensityAsync(TargetLayerId, new FeatureQuery(), density);

        await act.Should().ThrowAsync<ArgumentException>();
        harness.AssertNoDatabaseAccess();
    }

    [UnitTest]
    public async Task QuerySpatialJoinAsync_WithoutPolicies_BuildsTheSameSqlAsBefore()
    {
        var harness = new Harness();
        var join = Join() with { CarryFields = ["name"], OutStatistics = [Sum("population")] };

        await harness.Reader.QuerySpatialJoinAsync(TargetLayerId, new FeatureQuery(), join);

        var expected = CreateQueryBuilder().BuildSpatialJoinQuery(TargetLayerId, new FeatureQuery(), join);
        harness.Sql.Should().Be(expected.Sql);
        harness.Parameters.Should().Equal(expected.WhereParameters);
    }

    [UnitTest]
    public async Task QueryClustersAsync_WithoutPolicies_BuildsTheSameSqlAsBefore()
    {
        var harness = new Harness();

        await harness.Reader.QueryClustersAsync(TargetLayerId, new FeatureQuery(), KMeans());

        var expected = CreateQueryBuilder().BuildClusterQuery(TargetLayerId, new FeatureQuery(), KMeans());
        harness.Sql.Should().Be(expected.Sql);
        harness.Sql.Should().NotContain("attributes - $");
    }

}
