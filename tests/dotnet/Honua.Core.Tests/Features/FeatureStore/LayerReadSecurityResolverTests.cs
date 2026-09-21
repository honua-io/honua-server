// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Moq;

namespace Honua.Core.Tests.Features.FeatureStore;

public sealed class LayerReadSecurityResolverTests
{
    private const int LayerId = 7;

    [Theory]
    [InlineData("region = @p0 AND tier = @p1", "(status = @p0) AND (region = @p1 AND tier = @p2)")]
    [InlineData("region = $1 AND tier = $2", "(status = @p0) AND (region = $2 AND tier = $3)")]
    public async Task ApplyAsync_PermanentFilterAndRowPredicate_CombinesAndRenumbersPlaceholders(string rowSql, string expectedSql)
    {
        var resolver = CreateResolver(
            permanentFilter: new SqlFragment("status = @p0", ["active"]),
            rowFilter: new SqlFragment(rowSql, ["west", "gold"]));

        var query = await resolver.ApplyAsync(LayerId, new FeatureQuery(), CancellationToken.None);

        query.EnforcedSqlFilter.Should().NotBeNull();
        query.EnforcedSqlFilter!.Sql.Should().Be(expectedSql);
        query.EnforcedSqlFilter.Parameters.Should().Equal("active", "west", "gold");
    }

    [Fact]
    public async Task ApplyAsync_ResourceKeyed_ResolvesTheSamePolicyAsTheLayerIdLookup()
    {
        var resolver = CreateResolver(
            permanentFilter: new SqlFragment("status = @p0", ["active"]),
            rowFilter: new SqlFragment("region = @p0", ["west"]),
            maskedFields: ["secret"]);

        var byLayerId = await resolver.ApplyAsync(LayerId, new FeatureQuery(), CancellationToken.None);
        var byResource = await resolver.ApplyAsync(CreateResource(withPermanentFilter: true), new FeatureQuery(), CancellationToken.None);

        byResource.EnforcedSqlFilter!.Sql.Should().Be(byLayerId.EnforcedSqlFilter!.Sql);
        byResource.EnforcedSqlFilter.Parameters.Should().Equal(byLayerId.EnforcedSqlFilter.Parameters);
        byResource.EnforcedMaskedFields.Should().Equal(byLayerId.EnforcedMaskedFields);
        byResource.EnforcedMaskedFields.Should().Equal(ImmutableArray.Create("secret"));
    }

    [Fact]
    public async Task ApplyAsync_ResourceKeyed_UsesTheBoundResourceWhenStorageLayerIdsCollide()
    {
        var first = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-first", Name = "first" },
            StorageBindingIds = ["binding-first"]
        };
        var second = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-second", Name = "second" },
            StorageBindingIds = ["binding-second"]
        };
        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            Resources = [first, second],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-first", Name = "binding-first" },
                    ResourceId = first.Metadata.Id,
                    StorageType = MetadataV2StorageType.RelationalTable,
                    Locator = "public.first",
                    StorageLayerId = LayerId
                },
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-second", Name = "binding-second" },
                    ResourceId = second.Metadata.Id,
                    StorageType = MetadataV2StorageType.RelationalTable,
                    Locator = "public.second",
                    StorageLayerId = LayerId
                }
            ]
        };
        var rowSource = new Mock<IRowLevelSecurityFilterSource>();
        rowSource
            .Setup(source => source.ResolveAsync(second, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SqlFragment("tenant = @p0", ["second"]));
        var maskSource = new Mock<IFieldMaskSource>();
        maskSource
            .Setup(source => source.ResolveAsync(second, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImmutableArray.Create("secret"));
        var resolver = new LayerReadSecurityResolver(
            new StubGraphProvider(new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UtcNow)),
            filterExpressionService: null,
            rowSource.Object,
            maskSource.Object);

        var applied = await resolver.ApplyAsync(second, new FeatureQuery(), CancellationToken.None);

        applied.EnforcedSqlFilter!.Sql.Should().Be("tenant = @p0");
        applied.EnforcedSqlFilter.Parameters.Should().Equal("second");
        applied.EnforcedMaskedFields.Should().Equal(ImmutableArray.Create("secret"));
    }

    [Fact]
    public async Task ApplyAsync_NothingResolves_ReturnsTheQueryUnchanged()
    {
        var resolver = CreateResolver(permanentFilter: null, rowFilter: null);
        var query = new FeatureQuery { Where = "name = 'a'" };

        var applied = await resolver.ApplyAsync(LayerId, query, CancellationToken.None);

        applied.Should().Be(query);
    }

    [Fact]
    public async Task EnsureNoUnenforcedPolicyAsync_NothingResolves_Completes()
    {
        var resolver = CreateResolver(permanentFilter: null, rowFilter: null);

        var act = () => resolver.EnsureNoUnenforcedPolicyAsync("Test", LayerId, boundResource: null, rejectPermanentFilter: true, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureNoUnenforcedPolicyAsync_RowPredicateResolves_ThrowsNotSupported()
    {
        var resolver = CreateResolver(permanentFilter: null, rowFilter: new SqlFragment("region = @p0", ["west"]));

        var act = () => resolver.EnsureNoUnenforcedPolicyAsync("Test", LayerId, boundResource: null, rejectPermanentFilter: false, CancellationToken.None);

        (await act.Should().ThrowAsync<NotSupportedException>())
            .WithMessage("*row-level security*Test provider cannot enforce it*");
    }

    [Fact]
    public async Task EnsureNoUnenforcedPolicyAsync_FieldMaskResolves_ThrowsNotSupported()
    {
        var resolver = CreateResolver(permanentFilter: null, rowFilter: null, maskedFields: ["secret"]);

        var act = () => resolver.EnsureNoUnenforcedPolicyAsync("Test", LayerId, boundResource: null, rejectPermanentFilter: false, CancellationToken.None);

        (await act.Should().ThrowAsync<NotSupportedException>())
            .WithMessage("*field-mask*Test provider cannot enforce it*");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnsureNoUnenforcedPolicyAsync_PermanentFilterConfigured_ThrowsOnlyWhenTheProviderDoesNotApplyIt(bool rejectPermanentFilter)
    {
        var resolver = CreateResolver(permanentFilter: new SqlFragment("status = @p0", ["active"]), rowFilter: null);

        var act = () => resolver.EnsureNoUnenforcedPolicyAsync("Test", LayerId, boundResource: null, rejectPermanentFilter, CancellationToken.None);

        if (rejectPermanentFilter)
        {
            (await act.Should().ThrowAsync<NotSupportedException>()).WithMessage("*permanent*Test provider cannot enforce it*");
        }
        else
        {
            await act.Should().NotThrowAsync();
        }
    }

    [Fact]
    public async Task EnsureNoUnenforcedPolicyAsync_BoundResource_IsUsedWithoutALayerIdLookup()
    {
        var rowSource = new Mock<IRowLevelSecurityFilterSource>();
        var bound = CreateResource(withPermanentFilter: false);
        rowSource
            .Setup(source => source.ResolveAsync(bound, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SqlFragment("region = @p0", ["west"]));
        var resolver = new LayerReadSecurityResolver(v2Provider: null, filterExpressionService: null, rowSource.Object, fieldMaskSource: null);

        var act = () => resolver.EnsureNoUnenforcedPolicyAsync("Test", LayerId, bound, rejectPermanentFilter: false, CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private static LayerReadSecurityResolver CreateResolver(
        SqlFragment? permanentFilter,
        SqlFragment? rowFilter,
        string[]? maskedFields = null)
    {
        var resource = CreateResource(withPermanentFilter: permanentFilter is not null);
        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            Resources = [resource],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-parcels", Name = "binding-parcels" },
                    ResourceId = resource.Metadata.Id,
                    StorageType = MetadataV2StorageType.RelationalTable,
                    Locator = "public.parcels",
                    StorageLayerId = LayerId
                }
            ]
        };
        var graphProvider = new StubGraphProvider(new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UtcNow));

        var expression = new PropertyReference("status");
        var filterService = new Mock<IFilterExpressionService>();
        filterService
            .Setup(service => service.ParseAndNormalize(It.IsAny<FilterLanguage>(), It.IsAny<string?>(), It.IsAny<MetadataV2Resource>()))
            .Returns(FilterParseResult.Success(expression));
        filterService
            .Setup(service => service.Translate(It.IsAny<FilterExpression?>(), It.IsAny<MetadataV2Resource>()))
            .Returns(FilterTranslationResult.Success(expression, permanentFilter));

        var rowSource = new Mock<IRowLevelSecurityFilterSource>();
        rowSource
            .Setup(source => source.ResolveAsync(It.IsAny<MetadataV2Resource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rowFilter);

        var maskSource = new Mock<IFieldMaskSource>();
        maskSource
            .Setup(source => source.ResolveAsync(It.IsAny<MetadataV2Resource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(maskedFields is null ? ImmutableArray<string>.Empty : ImmutableArray.Create(maskedFields));

        return new LayerReadSecurityResolver(graphProvider, filterService.Object, rowSource.Object, maskSource.Object);
    }

    private static MetadataV2Resource CreateResource(bool withPermanentFilter) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "res-parcels", Name = "parcels" },
        Type = MetadataV2ResourceType.FeatureDataset,
        StorageBindingIds = ["binding-parcels"],
        PermanentFilter = withPermanentFilter
            ? new MetadataV2PermanentFilter { Expression = "status = 'active'", Language = MetadataV2PermanentFilterLanguages.ArcGisSql }
            : null
    };

    private sealed class StubGraphProvider(MetadataV2GraphSnapshot snapshot) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(null);
    }
}
