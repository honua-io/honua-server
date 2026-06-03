// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class RelatedRecordsServiceTests
{
    // Regression (#1431 + #1452): queryRelatedRecords must populate the field schema
    // from the related layer (so clients can map the returned attributes) and, per the
    // Esri spec, those fields live at the response top level while each group's
    // relatedRecords is a flat array of records.
    [Fact]
    public void GroupRelatedRecords_PopulatesTopLevelFieldsAndFlatRecords()
    {
        var sut = CreateSut(Substitute.For<IRelationshipStore>());

        var relationship = new MetadataV2Relationship
        {
            Id = "rel-1",
            RelatedResourceId = "child",
            OriginField = "objectid",
            DestinationField = "parent_id"
        };

        var relatedFeature = Feature.Create(
            10,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["objectid"] = 10L,
                ["parent_id"] = 1L,
                ["label"] = "child-a"
            }.ToImmutableDictionary());

        var result = QueryResult<Feature>.Create(1, [relatedFeature]);

        var grouped = sut.GroupRelatedRecords(
            result,
            objectIds: [1],
            relationship,
            objectIdFieldName: "objectid",
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null,
            outFields: null,
            relatedResource: CreateRelatedResource());

        // Field schema is carried once at the top level (#1431 population preserved).
        grouped.Fields.Should().NotBeEmpty();
        grouped.Fields.Select(field => field.Name)
            .Should().Contain("label")
            .And.Contain("objectid");
        grouped.ObjectIdFieldName.Should().Be("objectid");

        // Each group's relatedRecords is now a flat array of records (#1452).
        var group = grouped.Groups.Should().ContainSingle().Which;
        group.ObjectId.Should().Be(1L);
        group.RelatedRecords.Should().NotBeNull();
        group.RelatedRecords!.Should().ContainSingle();
        group.RelatedRecords[0].Attributes.Should().ContainKey("label");
    }

    private static MetadataV2Resource CreateRelatedResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "child", Name = "child" },
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                new MetadataV2Field { Name = "parent_id", Type = MetadataV2FieldType.Integer, Nullable = false },
                new MetadataV2Field { Name = "label", Type = MetadataV2FieldType.String, Length = 128 }
            ]
        };

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
        => RelatedQuery.ForObjects(
            [1],
            relatedLayerId: 2,
            originForeignKeyField: "origin_id",
            destinationForeignKeyField: "destination_id");
}
