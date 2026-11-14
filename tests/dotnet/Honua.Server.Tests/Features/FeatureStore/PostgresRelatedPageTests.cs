// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using NSubstitute;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.FeatureStore;

public partial class PostgresFeatureStoreIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    public async Task QueryRelatedPageAsync_SharedKeysAndEmptyOrigins_ResumesWithoutLoss()
    {
        var store = CreateFeatureStore();
        async Task<Feature> AddAsync(int layerId, string field, object? value) =>
            await store.CreateAsync(layerId, Feature.Create(0, layerId == LinesLayerId ? CreateLineStringWkb([(0, 0), (1, 1)]) : CreatePointWkb(0, 0),
                ImmutableDictionary<string, object?>.Empty.Add(field, value)), CancellationToken.None);
        var originA = await AddAsync(PointsLayerId, "category", "page-key");
        var originB = await AddAsync(PointsLayerId, "category", "page-key");
        var empty = await AddAsync(PointsLayerId, "category", null);
        var childA = await AddAsync(LinesLayerId, "road_type", "page-key");
        var childB = await AddAsync(LinesLayerId, "road_type", "page-key");
        var query = RelatedQuery.ForObjects([empty.Id, originB.Id, originA.Id], LinesLayerId, "category", "road_type")
            with { OutFields = ["objectid"] };
        var positions = new List<RelatedFeaturePosition>();
        RelatedPageCursor? cursor = null;
        for (var pageNumber = 0; pageNumber < 5; pageNumber++)
        {
            var page = await store.QueryRelatedPageAsync(PointsLayerId, new RelatedPageQuery(query, 2, cursor));
            page.Positions.Length.Should().BeLessThanOrEqualTo(2);
            positions.AddRange(page.Positions);
            if (!page.HasMore)
            {
                break;
            }
            var last = page.Positions.Last();
            cursor = new RelatedPageCursor(last.OriginId, last.RelatedFeature?.Id);
        }
        positions.Select(p => (p.OriginId, p.RelatedFeature?.Id)).Should().Equal(
            (originA.Id, (long?)childA.Id), (originA.Id, childB.Id),
            (originB.Id, childA.Id), (originB.Id, childB.Id), (empty.Id, null));
        positions.Where(p => p.RelatedFeature.HasValue).Should().OnlyContain(p =>
            p.RelatedFeature!.Value.Attributes.Keys.SequenceEqual(new[] { "objectid" }));
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    public async Task QueryRelatedPageAsync_ObjectIdJoin_EmitsUnmatchedOrigin()
    {
        var store = CreateFeatureStore();
        var parent = await store.CreateAsync(LinesLayerId, Feature.Create(0, CreateLineStringWkb([(0, 0), (1, 1)])), CancellationToken.None);
        var child = await store.CreateAsync(PointsLayerId, Feature.Create(0, CreatePointWkb(0, 0),
            ImmutableDictionary<string, object?>.Empty.Add("category", parent.Id)), CancellationToken.None);
        var empty = await store.CreateAsync(PointsLayerId, Feature.Create(0, CreatePointWkb(0, 0)), CancellationToken.None);
        var query = RelatedQuery.ForObjects([child.Id, empty.Id], LinesLayerId, "category", "object_id");

        var page = await store.QueryRelatedPageAsync(PointsLayerId, new RelatedPageQuery(query, 10));

        page.Positions.Select(p => (p.OriginId, p.RelatedFeature?.Id)).Should().Equal(
            (child.Id, (long?)parent.Id), (empty.Id, null));
        page.HasMore.Should().BeFalse();
    }
    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    public async Task QueryRelatedPageAsync_RowFiltersOnBothLayers_HidesOriginsAndChildren()
    {
        var writer = CreateFeatureStore();
        var attributes = ImmutableDictionary<string, object?>.Empty.Add("category", "restricted-page");
        var visible = await writer.CreateAsync(PointsLayerId, Feature.Create(0, CreatePointWkb(0, 0), attributes), CancellationToken.None);
        var hidden = await writer.CreateAsync(PointsLayerId, Feature.Create(0, CreatePointWkb(0, 0), attributes), CancellationToken.None);
        var child = await writer.CreateAsync(LinesLayerId, Feature.Create(0, CreateLineStringWkb([(0, 0), (1, 1)]),
            ImmutableDictionary<string, object?>.Empty.Add("road_type", "restricted-page")), CancellationToken.None);
        var filters = Substitute.For<IRowLevelSecurityFilterSource>();
        filters.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(call => new SqlFragment("objectid <> @p0", [
                call.Arg<MetadataV2Resource>().Metadata.Id == "res-test-points" ? hidden.Id : child.Id]));
        var query = RelatedQuery.ForObjects([visible.Id, hidden.Id], LinesLayerId, "category", "road_type");

        var page = await CreateFeatureStore(rowFilters: filters).QueryRelatedPageAsync(PointsLayerId, new RelatedPageQuery(query, 10));

        page.Positions.Should().ContainSingle().Which.Should().Be(new RelatedFeaturePosition(visible.Id, null));
        page.HasMore.Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    public async Task QueryRelatedPageAsync_MaskedJoinKeyAlias_IsRejected()
    {
        var masks = Substitute.For<IFieldMaskSource>();
        masks.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create("objectid"));
        var query = RelatedQuery.ForObjects([1], LinesLayerId, "object_id", "road_type");
        var action = () => CreateFeatureStore(fieldMasks: masks)
            .QueryRelatedPageAsync(PointsLayerId, new RelatedPageQuery(query, 10));

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    public async Task QueryRelatedPageAsync_MaskedRelatedAttribute_IsRemoved()
    {
        var writer = CreateFeatureStore();
        var origin = await writer.CreateAsync(PointsLayerId, Feature.Create(0, CreatePointWkb(0, 0),
            ImmutableDictionary<string, object?>.Empty.Add("category", "masked-page")), CancellationToken.None);
        await writer.CreateAsync(LinesLayerId, Feature.Create(0, CreateLineStringWkb([(0, 0), (1, 1)]),
            ImmutableDictionary<string, object?>.Empty.Add("road_type", "masked-page").Add("length_km", 7)), CancellationToken.None);
        var masks = Substitute.For<IFieldMaskSource>();
        masks.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create("length_km"));
        var query = RelatedQuery.ForObjects([origin.Id], LinesLayerId, "category", "road_type");

        var page = await CreateFeatureStore(fieldMasks: masks).QueryRelatedPageAsync(PointsLayerId, new RelatedPageQuery(query, 10));

        page.Positions.Should().ContainSingle().Which.RelatedFeature!.Value.Attributes.Should().NotContainKey("length_km");
    }
}
