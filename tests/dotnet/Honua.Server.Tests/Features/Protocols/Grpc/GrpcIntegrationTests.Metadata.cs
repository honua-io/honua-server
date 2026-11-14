// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Grpc.Core;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeLayer")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/DescribeLayer")]
    public async Task DescribeLayer_ReturnsNativeSchemaWithoutFeatureRows()
    {
        var response = await _client!.DescribeLayerAsync(new Proto.DescribeLayerRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId
        }, _headers);

        response.Layer.ServiceId.Should().Be(WebAppFixture.TestServiceId);
        response.Layer.LayerId.Should().Be(WebAppFixture.TestLayerId);
        response.Layer.ObjectIdField.Should().Be("objectid");
        response.Layer.Fields.Select(field => field.Definition.Name)
            .Should().Contain(["objectid", "name", "category"]);
        response.Layer.Capabilities.Query.Should().BeTrue();
        response.Layer.Capabilities.QueryStream.Should().BeTrue();
        response.Layer.Capabilities.UploadAttachments.Should().BeFalse();
        response.Layer.MaxRecordCount.Should().BePositive();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeService")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/DescribeService")]
    public async Task DescribeService_ReturnsDiscoverableLayerAndRejectsInvalidPaging()
    {
        var response = await _client!.DescribeServiceAsync(new Proto.DescribeServiceRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            PageSize = 1
        }, _headers);

        response.Service.ServiceId.Should().Be(WebAppFixture.TestServiceId);
        response.Layers.Should().ContainSingle();
        response.Service.Capabilities.Query.Should().BeTrue();
        var invalid = async () => await _client!.DescribeServiceAsync(new Proto.DescribeServiceRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            PageSize = -1
        }, _headers);
        (await invalid.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.InvalidArgument);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeLayer")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/DescribeLayer")]
    public async Task DescribeLayer_MissingLayer_ReturnsNotFound()
    {
        var call = async () => await _client!.DescribeLayerAsync(new Proto.DescribeLayerRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = int.MaxValue
        }, _headers);

        (await call.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [IntegrationTheory]
    [InlineData("not-a-token")]
    [InlineData("other-service\n0\n1")]
    [InlineData("test\n0\n-1")]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeService")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/DescribeService")]
    public async Task DescribeService_RejectsMalformedOrForeignPageTokens(string payload)
    {
        var call = async () => await _client!.DescribeServiceAsync(new Proto.DescribeServiceRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            PageToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
        }, _headers);

        (await call.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }
}
