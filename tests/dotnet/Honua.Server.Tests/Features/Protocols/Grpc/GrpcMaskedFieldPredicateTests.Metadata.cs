// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcMaskedFieldPredicateTests
{
    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeLayer")]
    public async Task DescribeLayer_ProjectedBbox_PreservesCoordinatesAndCrs()
    {
        var triple = CreateTriple();
        var resource = triple.Resource with
        {
            Spatial = triple.Resource.Spatial! with
            {
                SpatialReference = MetadataV2SpatialReference.WebMercator,
                Bbox = new() { West = -13700000, South = 5600000, East = -13600000, North = 5700000 }
            }
        };
        _resourceValidator.ValidateServiceLayerV2Async("test", 0, Arg.Any<CancellationToken>())
            .Returns(Honua.Core.Features.Validation.Abstractions.ResourceValidationResult.Success(triple with { Resource = resource }));
        using var context = CreateCallContext();

        var response = await _sut.DescribeLayer(new Proto.DescribeLayerRequest { ServiceId = "test", LayerId = 0 }, context);

        response.Layer.SpatialReference.Wkid.Should().Be(3857);
        response.Layer.Extent.SpatialReference.Wkid.Should().Be(3857);
        response.Layer.Extent.Xmin.Should().Be(-13700000);
        response.Layer.Extent.Ymax.Should().Be(5700000);
        AssertNoRead();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeLayer")]
    public async Task DescribeLayer_RetainsVisibleGeometryAndOmitsHiddenGeometry()
    {
        var triple = CreateTriple();
        var resource = triple.Resource with
        {
            SchemaFields = [.. triple.Resource.SchemaFields,
                new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry },
                new MetadataV2Field { Name = "private_shape", Type = MetadataV2FieldType.Geography, Hidden = true }]
        };
        _resourceValidator.ValidateServiceLayerV2Async("test", 0, Arg.Any<CancellationToken>())
            .Returns(Honua.Core.Features.Validation.Abstractions.ResourceValidationResult.Success(triple with { Resource = resource }));
        using var context = CreateCallContext();

        var response = await _sut.DescribeLayer(new Proto.DescribeLayerRequest { ServiceId = "test", LayerId = 0 }, context);

        response.Layer.Fields.Select(field => field.Definition.Name).Should().Equal("objectid", "name", "shape");
        response.Layer.Fields.Single(field => field.Definition.Name == "shape").Definition.FieldType
            .Should().Be(Proto.FieldType.Geometry);
        AssertNoRead();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeLayer")]
    public async Task DescribeLayer_OmitsMaskedFieldsAndDoesNotReadFeatureStorage()
    {
        using var context = CreateCallContext();
        var response = await _sut.DescribeLayer(new Proto.DescribeLayerRequest
        {
            ServiceId = "test",
            LayerId = 0
        }, context);

        response.Layer.Fields.Select(field => field.Definition.Name).Should().Equal("objectid", "name");
        response.Layer.ObjectIdField.Should().Be("objectid");
        response.Layer.Capabilities.Create.Should().BeFalse("this layer has no managed storage binding");
        response.Layer.Capabilities.Query.Should().BeTrue();
        AssertNoRead();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeLayer")]
    public async Task DescribeLayer_MaskedPrimaryKeyDoesNotLeakThroughFieldReferences()
    {
        _fieldMaskSource.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create("OBJECTID", MaskedField));
        using var context = CreateCallContext();
        var response = await _sut.DescribeLayer(new Proto.DescribeLayerRequest
        {
            ServiceId = "test",
            LayerId = 0
        }, context);

        response.Layer.Fields.Select(field => field.Definition.Name).Should().Equal("name");
        response.Layer.ObjectIdField.Should().BeEmpty();
        AssertNoRead();
    }
}
