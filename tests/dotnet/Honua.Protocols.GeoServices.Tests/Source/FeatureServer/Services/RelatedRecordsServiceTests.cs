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

    // #1396: queryRelatedRecords orderByFields must re-sort each origin object's
    // related records in-memory according to the requested field/direction.
    [Fact]
    public void GroupRelatedRecords_WithOrderByFieldsDescending_SortsRelatedRecords()
    {
        var sut = CreateSut(Substitute.For<IRelationshipStore>());

        var relationship = new MetadataV2Relationship
        {
            Id = "rel-1",
            RelatedResourceId = "child",
            OriginField = "objectid",
            DestinationField = "parent_id"
        };

        var first = Feature.Create(
            10,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["objectid"] = 10L,
                ["parent_id"] = 1L,
                ["label"] = "child-a"
            }.ToImmutableDictionary());
        var second = Feature.Create(
            11,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["objectid"] = 11L,
                ["parent_id"] = 1L,
                ["label"] = "child-c"
            }.ToImmutableDictionary());
        var third = Feature.Create(
            12,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["objectid"] = 12L,
                ["parent_id"] = 1L,
                ["label"] = "child-b"
            }.ToImmutableDictionary());

        var result = QueryResult<Feature>.Create(3, [first, second, third]);

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
            relatedResource: CreateRelatedResource(),
            orderBy: [new OrderByClause("label", ascending: false)]);

        var group = grouped.Groups.Should().ContainSingle().Which;
        group.RelatedRecords.Should().NotBeNull();
        group.RelatedRecords!.Select(r => r.Attributes["label"])
            .Should().ContainInOrder("child-c", "child-b", "child-a");
    }

    // #1396: returnCountOnly must emit a per-source-object count and omit both the
    // per-record payload and the top-level field/geometry schema.
    [Fact]
    public void GroupRelatedRecords_WithReturnCountOnly_EmitsCountsAndOmitsRecords()
    {
        var sut = CreateSut(Substitute.For<IRelationshipStore>());

        var relationship = new MetadataV2Relationship
        {
            Id = "rel-1",
            RelatedResourceId = "child",
            OriginField = "objectid",
            DestinationField = "parent_id"
        };

        var firstChild = Feature.Create(
            10,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["objectid"] = 10L,
                ["parent_id"] = 1L,
                ["label"] = "child-a"
            }.ToImmutableDictionary());
        var secondChild = Feature.Create(
            11,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["objectid"] = 11L,
                ["parent_id"] = 1L,
                ["label"] = "child-b"
            }.ToImmutableDictionary());

        var result = QueryResult<Feature>.Create(2, [firstChild, secondChild]);

        var grouped = sut.GroupRelatedRecords(
            result,
            objectIds: [1, 2],
            relationship,
            objectIdFieldName: "objectid",
            returnGeometry: true,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null,
            outFields: null,
            relatedResource: CreateRelatedResource(),
            orderBy: null,
            returnCountOnly: true);

        grouped.Fields.Should().BeEmpty();
        grouped.GeometryType.Should().BeNull();
        grouped.SpatialReference.Should().BeNull();

        var groups = grouped.Groups;
        groups.Should().HaveCount(2);

        var withRecords = groups.Single(g => g.ObjectId == 1L);
        withRecords.Count.Should().Be(2);
        withRecords.RelatedRecords.Should().BeNull();

        var withoutRecords = groups.Single(g => g.ObjectId == 2L);
        withoutRecords.Count.Should().Be(0);
        withoutRecords.RelatedRecords.Should().BeNull();
    }

    // Regression: in the standard (non-count) queryRelatedRecords flow, a source object
    // with no related rows must still carry relatedRecords as an empty array rather than
    // null/omitted. The @arcgis/core JS SDK reads group.relatedRecords.length and throws
    // "Cannot read properties of undefined (reading 'length')" otherwise.
    [Fact]
    public void GroupRelatedRecords_WithNoRelatedRows_EmitsEmptyRelatedRecordsArray()
    {
        var sut = CreateSut(Substitute.For<IRelationshipStore>());

        var relationship = new MetadataV2Relationship
        {
            Id = "rel-1",
            RelatedResourceId = "child",
            OriginField = "objectid",
            DestinationField = "parent_id"
        };

        // One related row belongs to object 1; object 2 has none.
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
            objectIds: [1, 2],
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

        var withRecords = grouped.Groups.Single(g => g.ObjectId == 1L);
        withRecords.RelatedRecords.Should().NotBeNull();
        withRecords.RelatedRecords!.Should().ContainSingle();

        var withoutRecords = grouped.Groups.Single(g => g.ObjectId == 2L);
        withoutRecords.RelatedRecords.Should().NotBeNull();
        withoutRecords.RelatedRecords!.Should().BeEmpty();
    }

    // Regression (#1465): when the relationship's origin foreign key is NOT the object-id
    // field, the related row's destination key value differs from the origin object id.
    // Grouping must bucket related rows by the origin object id the storage layer stamped
    // onto each row (RelatedQuery.OriginObjectIdsAttribute), NOT by the destination key
    // value. Previously the grouping treated the destination key value as the origin
    // object id, so the relate only resolved when the foreign key happened to equal the
    // object id — failing for any origin object id whose key differs (e.g. large ids).
    [Fact]
    public void GroupRelatedRecords_WithNonObjectIdOriginKey_GroupsByStampedOriginObjectId()
    {
        var sut = CreateSut(Substitute.For<IRelationshipStore>());

        var relationship = new MetadataV2Relationship
        {
            Id = "rel-1",
            RelatedResourceId = "child",
            OriginField = "ext_key",
            DestinationField = "ext_key"
        };

        // The related row's destination key ('K-300208') is NOT the origin object id
        // (300208). Only the stamped origin id ties the row back to its origin.
        var originIds300208 = new[] { 300208L };
        var relatedFeature = Feature.Create(
            10,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["objectid"] = 10L,
                ["ext_key"] = "K-300208",
                ["label"] = "child-a",
                [RelatedQuery.OriginObjectIdsAttribute] = originIds300208
            }.ToImmutableDictionary());

        var result = QueryResult<Feature>.Create(1, [relatedFeature]);

        var grouped = sut.GroupRelatedRecords(
            result,
            objectIds: [300208],
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

        var group = grouped.Groups.Should().ContainSingle().Which;
        group.ObjectId.Should().Be(300208L);
        group.RelatedRecords.Should().NotBeNull();
        group.RelatedRecords!.Should().ContainSingle();
        group.RelatedRecords[0].Attributes.Should().ContainKey("label");

        // The internal origin-id stamp must never leak into the emitted attributes.
        group.RelatedRecords[0].Attributes.Should()
            .NotContainKey(RelatedQuery.OriginObjectIdsAttribute);
    }

    // A single foreign-key value can resolve to multiple origin object ids; the related
    // row must then appear under every matching origin group.
    [Fact]
    public void GroupRelatedRecords_WithStampSpanningMultipleOrigins_AppearsInEachGroup()
    {
        var sut = CreateSut(Substitute.For<IRelationshipStore>());

        var relationship = new MetadataV2Relationship
        {
            Id = "rel-1",
            RelatedResourceId = "child",
            OriginField = "ext_key",
            DestinationField = "ext_key"
        };

        var originIds7And9 = new[] { 7L, 9L };
        var relatedFeature = Feature.Create(
            10,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["objectid"] = 10L,
                ["ext_key"] = "shared",
                ["label"] = "child-a",
                [RelatedQuery.OriginObjectIdsAttribute] = originIds7And9
            }.ToImmutableDictionary());

        var result = QueryResult<Feature>.Create(1, [relatedFeature]);

        var grouped = sut.GroupRelatedRecords(
            result,
            objectIds: [7, 9],
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

        grouped.Groups.Should().HaveCount(2);
        grouped.Groups.Single(g => g.ObjectId == 7L).RelatedRecords.Should().ContainSingle();
        grouped.Groups.Single(g => g.ObjectId == 9L).RelatedRecords.Should().ContainSingle();
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
