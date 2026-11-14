// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcIntegrationTests
{
    [IntegrationTheory]
    [InlineData("AbortAttachmentUpload")]
    [InlineData("BeginAttachmentUpload")]
    [InlineData("CommitAttachmentUpload")]
    [InlineData("DeleteAttachment")]
    [InlineData("DescribeLayer")]
    [InlineData("DescribeService")]
    [InlineData("GetAttachmentUrl")]
    [InlineData("ListAttachments")]
    [InlineData("UploadAttachment")]
    [InlineData("UploadAttachmentChunk")]
    [Endpoint("POST /geospatial.v1.FeatureService/AbortAttachmentUpload")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/AbortAttachmentUpload")]
    [Endpoint("POST /geospatial.v1.FeatureService/BeginAttachmentUpload")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/BeginAttachmentUpload")]
    [Endpoint("POST /geospatial.v1.FeatureService/CommitAttachmentUpload")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/CommitAttachmentUpload")]
    [Endpoint("POST /geospatial.v1.FeatureService/DeleteAttachment")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/DeleteAttachment")]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeLayer")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/DescribeLayer")]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeService")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/DescribeService")]
    [Endpoint("POST /geospatial.v1.FeatureService/GetAttachmentUrl")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/GetAttachmentUrl")]
    [Endpoint("POST /geospatial.v1.FeatureService/ListAttachments")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ListAttachments")]
    [Endpoint("POST /geospatial.v1.FeatureService/UploadAttachment")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/UploadAttachment")]
    [Endpoint("POST /geospatial.v1.FeatureService/UploadAttachmentChunk")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/UploadAttachmentChunk")]
    public async Task ContractRpc_WithoutImplementation_ReturnsUnimplemented(string method)
    {
        Func<Task> call = method switch
        {
            "AbortAttachmentUpload" => async () => await _client!.AbortAttachmentUploadAsync(new Proto.AbortAttachmentUploadRequest(), _headers),
            "BeginAttachmentUpload" => async () => await _client!.BeginAttachmentUploadAsync(new Proto.BeginAttachmentUploadRequest(), _headers),
            "CommitAttachmentUpload" => async () => await _client!.CommitAttachmentUploadAsync(new Proto.CommitAttachmentUploadRequest(), _headers),
            "DeleteAttachment" => async () => await _client!.DeleteAttachmentAsync(new Proto.DeleteAttachmentRequest(), _headers),
            "DescribeLayer" => async () => await _client!.DescribeLayerAsync(new Proto.DescribeLayerRequest(), _headers),
            "DescribeService" => async () => await _client!.DescribeServiceAsync(new Proto.DescribeServiceRequest(), _headers),
            "GetAttachmentUrl" => async () => await _client!.GetAttachmentUrlAsync(new Proto.GetAttachmentUrlRequest(), _headers),
            "ListAttachments" => async () => await _client!.ListAttachmentsAsync(new Proto.ListAttachmentsRequest(), _headers),
            "UploadAttachment" => async () => await _client!.UploadAttachmentAsync(new Proto.UploadAttachmentRequest(), _headers),
            "UploadAttachmentChunk" => async () => await _client!.UploadAttachmentChunkAsync(new Proto.UploadAttachmentChunkRequest(), _headers),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };

        var failure = await call.Should().ThrowAsync<RpcException>();
        failure.Which.StatusCode.Should().Be(StatusCode.Unimplemented);
    }
}
