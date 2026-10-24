// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Isolated related-record groups for a query-only composite. No hosted capture is required.
/// </summary>
public sealed class QueryOnlyCompositeRelatedRecordGroupTests
{
    [UnitTest]
    public void GroupRelatedRecords_EmitsGroupsForParentsZeroChildParentsAndAbsentParents()
    {
        var sut = new RelatedRecordsService(
            Substitute.For<IRelationshipStore>(),
            Options.Create(new LimitsOptions()));
        var relationship = new MetadataV2Relationship
        {
            Id = "summary",
            RelatedResourceId = "child",
            Composite = true,
            Role = "esriRelRoleOrigin",
            OriginField = "Join_ID",
            DestinationField = "Join_ID"
        };
        var parentWithChildren = new[] { 1L };
        var childA = Feature.Create(10, geometry: null, new Dictionary<string, object?>
        {
            ["objectid"] = 10L,
            ["Join_ID"] = 10L,
            ["label"] = "child-a",
            [RelatedQuery.OriginObjectIdsAttribute] = parentWithChildren
        }.ToImmutableDictionary());
        var childB = Feature.Create(11, geometry: null, new Dictionary<string, object?>
        {
            ["objectid"] = 11L,
            ["Join_ID"] = 10L,
            ["label"] = "child-b",
            [RelatedQuery.OriginObjectIdsAttribute] = parentWithChildren
        }.ToImmutableDictionary());
        var orphan = Feature.Create(84, geometry: null, new Dictionary<string, object?>
        {
            ["objectid"] = 84L,
            ["Join_ID"] = 99L,
            ["label"] = "orphan"
        }.ToImmutableDictionary());

        var grouped = sut.GroupRelatedRecords(
            QueryResult<Feature>.Create(3, [childA, childB, orphan]),
            objectIds: [1, 2, 3],
            relationship,
            objectIdFieldName: "objectid",
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null,
            outFields: null,
            relatedResource: new MetadataV2Resource
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "child", Name = "groupbySummary" },
                Type = MetadataV2ResourceType.Table,
                SchemaFields =
                [
                    new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                    new MetadataV2Field { Name = "Join_ID", Type = MetadataV2FieldType.Integer, Nullable = true },
                    new MetadataV2Field { Name = "label", Type = MetadataV2FieldType.String, Length = 64 }
                ]
            });

        grouped.Groups.Select(group => group.ObjectId).Should().Equal(1L, 2L, 3L);
        var withChildren = grouped.Groups.Single(group => group.ObjectId == 1L);
        withChildren.RelatedRecords.Should().NotBeNull();
        withChildren.RelatedRecords!.Select(record => record.Attributes!["label"]).Should().Equal("child-a", "child-b");
        withChildren.RelatedRecords.Should().OnlyContain(record =>
            !record.Attributes!.ContainsKey(RelatedQuery.OriginObjectIdsAttribute));

        var zeroChildren = grouped.Groups.Single(group => group.ObjectId == 2L);
        zeroChildren.RelatedRecords.Should().NotBeNull().And.BeEmpty();

        var absentParent = grouped.Groups.Single(group => group.ObjectId == 3L);
        absentParent.RelatedRecords.Should().NotBeNull().And.BeEmpty();
        grouped.Groups.SelectMany(group => group.RelatedRecords ?? []).Should().NotContain(record =>
            Equals(record.Attributes!["label"], "orphan"));
    }
}
