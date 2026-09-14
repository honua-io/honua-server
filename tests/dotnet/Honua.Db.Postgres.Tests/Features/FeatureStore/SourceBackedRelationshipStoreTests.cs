// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed class SourceBackedRelationshipStoreTests
{
    [Fact]
    public async Task QueryAsync_NonObjectIdKeys_PreservesSharedParentsAndProjection()
    {
        var origins = Substitute.For<IFeatureReader>();
        var children = Substitute.For<IFeatureReader>();
        origins.QueryAsync(13, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(3, [Row(901, 2055), Row(902, 2055), Row(903, 2068)]));
        children.QueryAsync(14, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(2, [Row(81, 2055), Row(82, 2068)]));
        var query = RelatedQuery.ForObjects([901, 902, 903], 14, "join_id", "join_id") with
        {
            OutFields = ["details"],
            SqlFilter = new SqlFragment("attributes->>'details' = @p0", ["flying low"])
        };

        var result = await SourceBackedRelationshipStore.QueryReadersAsync(origins, children, 13, query, CancellationToken.None);

        result.Items.Should().HaveCount(2);
        result.Items[0].Attributes[RelatedQuery.OriginObjectIdsAttribute].Should().BeEquivalentTo(new long[] { 901, 902 });
        result.Items[1].Attributes[RelatedQuery.OriginObjectIdsAttribute].Should().BeEquivalentTo(new long[] { 903 });
        result.Items.Should().OnlyContain(row => !row.Attributes.ContainsKey("join_id") && row.Attributes.ContainsKey("details"));
        await children.Received(1).QueryAsync(14, Arg.Is<FeatureQuery>(q =>
            q.OutFields!.Value.Contains("join_id") && q.SqlFilter!.Parameters.Count == 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_HiddenOrMissingOrigin_DoesNotReadChildren()
    {
        var origins = Substitute.For<IFeatureReader>();
        var children = Substitute.For<IFeatureReader>();
        origins.QueryAsync(13, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Empty());

        var result = await SourceBackedRelationshipStore.QueryReadersAsync(origins, children, 13,
            RelatedQuery.ForObjects([901], 14, "join_id", "join_id"), CancellationToken.None);

        result.Items.Should().BeEmpty();
        await children.DidNotReceiveWithAnyArgs().QueryAsync(default, default, default);
    }

    private static Feature Row(long id, int key) => Feature.Create(id, null,
        new Dictionary<string, object?> { ["join_id"] = key, ["details"] = "flying low" }.ToImmutableDictionary());
}
