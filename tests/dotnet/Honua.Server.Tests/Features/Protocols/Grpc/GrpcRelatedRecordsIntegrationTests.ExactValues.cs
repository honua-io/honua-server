// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcRelatedRecordsIntegrationTests
{
    [IntegrationTheory]
    [InlineData(MetadataV2FieldType.Decimal, "12345678901234567890.1000")]
    [InlineData(MetadataV2FieldType.TimestampOffset, "2026-11-15T00:01:02.1234567-07:00")]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryRelatedRecords")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryRelatedRecords")]
    public async Task RelatedRecords_ExactValueProjection_PreservesTextAndWireType(
        MetadataV2FieldType type, string text)
    {
        await _fixture.UpdateV2ResourceAsync(WebAppFixture.TestLayerId, resource => resource with
        {
            SchemaFields = [.. resource.SchemaFields, new MetadataV2Field
            {
                Name = "exact_value", Type = type, Nullable = true
            }]
        });
        var value = type == MetadataV2FieldType.Decimal
            ? new Proto.AttributeValue { DecimalValue = text }
            : new Proto.AttributeValue { TimestampOffsetValue = text };
        var request = new Proto.ApplyEditsRequest
        {
            ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId,
            Adds = { new Proto.Feature
            {
                Attributes =
                {
                    ["name"] = new Proto.AttributeValue { StringValue = "related-exact-value" },
                    ["category"] = new Proto.AttributeValue { StringValue = Guid.NewGuid().ToString("D") },
                    ["exact_value"] = value
                },
                Geometry = new Proto.Geometry { Point = new Proto.PointGeometry { X = -157.85, Y = 21.30 } }
            } }
        };
        var added = (await _client!.ApplyEditsAsync(request, _headers)).AddResults.Should().ContainSingle().Subject;
        added.Success.Should().BeTrue();

        var response = await _client.QueryRelatedRecordsAsync(new Proto.QueryRelatedRecordsRequest
        {
            ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId,
            RelationshipId = 77, ObjectIds = { added.ObjectId }, OutFields = { "EXACT_VALUE" }, ReturnGeometry = false
        }, _headers);

        var child = response.Groups.Should().ContainSingle().Subject.Features.Should().ContainSingle().Subject;
        child.Id.Should().Be(added.ObjectId);
        child.Attributes.Should().ContainSingle().Which.Key.Should().Be("exact_value");
        child.Attributes["exact_value"].Should().Be(value);
        response.Fields.Should().ContainSingle().Which.FieldType.Should().Be(
            type == MetadataV2FieldType.Decimal ? Proto.FieldType.Decimal : Proto.FieldType.TimestampOffset);
    }
}
