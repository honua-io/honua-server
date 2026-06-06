// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Postgres.Tests.Features.FeatureStore;

/// <summary>
/// Branch-versioning overlay read tests (#1272 Track B, ADR-0051). The central guarantee proved here is
/// the CITE firewall: for the DEFAULT version (null or <see cref="VersionContext.Default"/>) the builder
/// must emit byte-identical SQL to the non-versioned base path. A non-DEFAULT version overlays the
/// version_edits table onto DEFAULT.
/// </summary>
public sealed class FeatureQueryBuilderVersionTests
{
    private static readonly Guid SampleVersionId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void BuildSelectQuery_DefaultVersionNull_IsByteIdenticalToNonVersioned()
    {
        var builder = CreateQueryBuilder();
        var baseline = builder.BuildSelectQuery(layerId: 1, new FeatureQuery());

        var withNullVersion = builder.BuildSelectQuery(layerId: 1, new FeatureQuery { VersionContext = null });

        // Byte-identical SQL is the CITE firewall: the DEFAULT (null) version must emit the exact same
        // SQL as the non-versioned base query, with no overlay subquery.
        withNullVersion.Sql.Should().Be(baseline.Sql);
        withNullVersion.Sql.Should().Contain("WHERE layer_id = $1");
        withNullVersion.Sql.Should().NotContain("version_edits");
        withNullVersion.Sql.Should().NotContain("UNION ALL");
        withNullVersion.WhereParameters.Should().Equal(baseline.WhereParameters);
    }

    [Fact]
    public void BuildSelectQuery_ExplicitDefaultVersion_IsByteIdenticalToNonVersioned()
    {
        var builder = CreateQueryBuilder();
        var baseline = builder.BuildSelectQuery(layerId: 1, new FeatureQuery());

        var withDefault = builder.BuildSelectQuery(layerId: 1, new FeatureQuery { VersionContext = VersionContext.Default });

        withDefault.Sql.Should().Be(baseline.Sql);
        withDefault.Sql.Should().NotContain("version_edits");
    }

    [Fact]
    public void BuildSelectQuery_BranchVersion_EmitsOverlayUnionAndBindsVersionId()
    {
        var builder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            VersionContext = VersionContext.ForVersion(SampleVersion())
        };

        var result = builder.BuildSelectQuery(layerId: 1, query);

        // Overlay shape: base-minus-shadowed UNION ALL overlay-non-deletes, parameterized on version_id.
        result.Sql.Should().Contain("honua.version_edits");
        result.Sql.Should().Contain("NOT EXISTS");
        result.Sql.Should().Contain("UNION ALL");
        result.Sql.Should().Contain("operation <> 3");
        result.Sql.Should().Contain("AS features WHERE layer_id = $1");
        result.WhereParameters.Should().Contain(SampleVersionId);
    }

    [Fact]
    public void BuildCountQuery_DefaultVersion_IsByteIdentical()
    {
        var builder = CreateQueryBuilder();
        var baseline = builder.BuildCountQuery(layerId: 7, new FeatureQuery());

        var withDefault = builder.BuildCountQuery(layerId: 7, new FeatureQuery { VersionContext = VersionContext.Default });

        withDefault.Sql.Should().Be(baseline.Sql);
        withDefault.Sql.Should().NotContain("version_edits");
    }

    [Fact]
    public void BuildCountQuery_BranchVersion_OverlaysVersionEdits()
    {
        var builder = CreateQueryBuilder();
        var query = new FeatureQuery { VersionContext = VersionContext.ForVersion(SampleVersion()) };

        var result = builder.BuildCountQuery(layerId: 7, query);

        result.Sql.Should().Contain("honua.version_edits");
        result.WhereParameters.Should().Contain(SampleVersionId);
    }

    [Fact]
    public void BuildObjectIdsQuery_BranchVersion_OverlaysVersionEdits()
    {
        var builder = CreateQueryBuilder();
        var query = new FeatureQuery { VersionContext = VersionContext.ForVersion(SampleVersion()) };

        var result = builder.BuildObjectIdsQuery(layerId: 2, query);

        result.Sql.Should().Contain("honua.version_edits");
        result.WhereParameters.Should().Contain(SampleVersionId);
    }

    [Fact]
    public void BuildStatisticsQuery_BranchVersion_IsNotSupported()
    {
        var builder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            VersionContext = VersionContext.ForVersion(SampleVersion()),
            OutStatistics = ImmutableArray.Create(new StatisticDefinition
            {
                StatisticType = StatisticType.Count,
                OnStatisticField = "objectid",
                OutStatisticFieldName = "cnt"
            })
        };

        var act = () => builder.BuildStatisticsQuery(layerId: 1, query);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void BuildSelectQuery_BranchVersionNearestNeighbor_IsNotSupported()
    {
        var builder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            VersionContext = VersionContext.ForVersion(SampleVersion()),
            SpatialFilter = SpatialFilter.CreateKnnFilter(new byte[] { 1, 2, 3 }, count: 5)
        };

        var act = () => builder.BuildSelectGeoJsonQuery(layerId: 1, query);

        act.Should().Throw<NotSupportedException>();
    }

    private static GdbVersion SampleVersion() => new()
    {
        VersionId = SampleVersionId,
        VersionName = "sde.QA",
        Owner = "sde",
        Access = VersionAccess.Public,
        State = VersionState.Active,
        CommonAncestorGeneration = 5,
        BranchGeneration = 9,
        CreatedAt = DateTimeOffset.UtcNow,
        ModifiedAt = DateTimeOffset.UtcNow,
    };

    private static FeatureQueryBuilder CreateQueryBuilder()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        return new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);
    }
}
