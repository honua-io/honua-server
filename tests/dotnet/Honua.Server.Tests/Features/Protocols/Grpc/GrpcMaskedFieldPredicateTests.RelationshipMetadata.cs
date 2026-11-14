// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcMaskedFieldPredicateTests
{
    [UnitTheory]
    [InlineData("unused", "unused", 1)]
    [InlineData("parent_id", "unused", 0)]
    [InlineData("unused", "objectid", 0)]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeLayer")]
    public async Task DescribeLayer_DestinationRelationship_UsesLocalAndRelatedKeys(
        string localMask, string relatedMask, int expectedCount)
    {
        var triple = DestinationRelationshipTriple();
        _resourceValidator.ValidateServiceLayerV2Async("test", 0, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(triple));
        _fieldMaskSource.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(call => ImmutableArray.Create(call.Arg<MetadataV2Resource>().Metadata.Id == _resource.Metadata.Id
                ? localMask : relatedMask));
        using var context = CreateCallContext();

        var response = await _sut.DescribeLayer(new() { ServiceId = "test", LayerId = 0 }, context);

        response.Layer.Relationships.Should().HaveCount(expectedCount);
        if (expectedCount > 0)
        {
            response.Layer.Relationships[0].OriginKeyField.Should().Be("parent_id");
            response.Layer.Relationships[0].DestinationKeyField.Should().Be("objectid");
            response.Layer.Relationships[0].Role.Should().Be(Geospatial.V1.RelationshipRole.Destination);
        }
        AssertNoRead();
    }

    private static MetadataV2ServiceLayerTriple DestinationRelationshipTriple()
    {
        var triple = CreateTriple();
        var parent = _resource with { Metadata = _resource.Metadata with { Id = "parent" } };
        var child = _resource with
        {
            SchemaFields = [.. _resource.SchemaFields, new() { Name = "parent_id", Type = MetadataV2FieldType.Integer }],
            Relationships = [new()
            {
                Id = "7", Name = "parent", RelatedResourceId = parent.Metadata.Id, Role = "destination",
                OriginField = "parent_id", DestinationField = "objectid"
            }]
        };
        var parentPublication = triple.Publication with
        {
            Metadata = triple.Publication.Metadata with { Id = "parent-publication" },
            ResourceId = parent.Metadata.Id,
            Identifier = new() { Value = "1", IsNumeric = true }
        };
        var snapshot = new MetadataV2GraphSnapshot(new()
        {
            Revision = 1,
            Environment = "test",
            Resources = [child, parent],
            Services = [_service],
            Publications = [triple.Publication, parentPublication]
        }, "relationships", DateTimeOffset.UtcNow);
        return triple with { Resource = child, Snapshot = snapshot };
    }
}
