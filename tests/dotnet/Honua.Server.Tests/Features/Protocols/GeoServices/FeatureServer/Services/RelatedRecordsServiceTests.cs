// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class RelatedRecordsServiceTests
{
    [Fact]
    public async Task ExecuteRelatedQueryAsync_WhenStoreThrowsArgumentException_ThrowsInvalidOperationException()
    {
        var relationshipStore = Substitute.For<IRelationshipStore>();
        relationshipStore.QueryRelatedAsync(Arg.Any<int>(), Arg.Any<RelatedQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<QueryResult<Feature>>(new ArgumentException("Invalid related query")));

        var sut = CreateSut(relationshipStore);

        Func<Task> act = () => sut.ExecuteRelatedQueryAsync(1, CreateRelatedQuery(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Invalid related query.");
    }

    [Fact]
    public async Task ExecuteRelatedQueryAsync_WhenStoreThrowsSqlWordedException_PropagatesOriginalException()
    {
        var expected = new TimeoutException("SQL parser crash");
        var relationshipStore = Substitute.For<IRelationshipStore>();
        relationshipStore.QueryRelatedAsync(Arg.Any<int>(), Arg.Any<RelatedQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<QueryResult<Feature>>(expected));

        var sut = CreateSut(relationshipStore);

        Func<Task> act = () => sut.ExecuteRelatedQueryAsync(1, CreateRelatedQuery(), CancellationToken.None);

        var thrown = await act.Should().ThrowExactlyAsync<TimeoutException>();
        thrown.Which.Should().BeSameAs(expected);
    }

    private static RelatedRecordsService CreateSut(IRelationshipStore relationshipStore)
    {
        return new RelatedRecordsService(relationshipStore, Options.Create(new LimitsOptions()));
    }

    private static RelatedQuery CreateRelatedQuery()
    {
        var relationship = Relationship.Create(
            relationshipId: 1,
            name: "test",
            relatedLayerId: 2,
            relationshipType: "esriRelRoleOrigin",
            originForeignKeyField: "origin_id",
            destinationForeignKeyField: "destination_id");

        return new RelatedQuery
        {
            ObjectIds = [1],
            Relationship = relationship
        };
    }
}
