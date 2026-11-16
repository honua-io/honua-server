// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcIntegrationTests
{
    private readonly IAttachmentNativeMetadataStore _attachmentMetadata = Substitute.For<IAttachmentNativeMetadataStore>();
    private readonly IAttachmentStore _attachments = Substitute.For<IAttachmentStore>();
    private readonly ICloudFileStorage _attachmentStorage = Substitute.For<ICloudFileStorage>();
    private static readonly Guid AttachmentGlobalId = Guid.Parse("1841c89c-d3f4-46e1-9991-93a40a01be13");

    private void ConfigureAttachmentReadServices()
    {
        var metadata = new AttachmentNativeMetadata(AttachmentGlobalId, ["photo", "original"], SHA256.HashData("test"u8));
        var attachment = Attachment.CreateForUpload(23, 1, WebAppFixture.TestLayerId,
            "photo.txt", "text/plain", 4, "private/attachment-object");
        _attachmentMetadata.QueryPageAsync(Arg.Any<AttachmentPageQuery>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new AttachmentPageItem(attachment, metadata) });
        _attachmentMetadata.GetNativeMetadataAsync(Arg.Any<AttachmentNativeMetadataQuery>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, AttachmentNativeMetadata> { [23] = metadata });
        _attachments.GetAsync(WebAppFixture.TestLayerId, 1, 23, Arg.Any<CancellationToken>()).Returns(attachment);
        _attachmentStorage.GetPresignedUrlAsync("private/attachment-object", Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns("https://downloads.example.test/attachment?signature=test");
        _fixture.ConfigureServices(services =>
        {
            services.AddSingleton(_attachmentMetadata);
            services.AddSingleton(_attachments);
            services.AddSingleton(_attachmentStorage);
        });
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/ListAttachments")]
    [Endpoint("POST /geospatial.v1.FeatureService/GetAttachmentUrl")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ListAttachments")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/GetAttachmentUrl")]
    public async Task AttachmentReads_GrpcWeb_ReturnNativeMetadataAndSignedDownload()
    {
        var parent = ExistingAttachmentParent();
        var response = await _client!.ListAttachmentsAsync(new Proto.ListAttachmentsRequest { Parent = parent }, _headers);
        var attachment = response.Attachments.Should().ContainSingle().Subject;
        attachment.AttachmentId.Should().Be(23);
        attachment.GlobalId.Should().Be(AttachmentGlobalId.ToString("D"));
        attachment.Keywords.Should().Equal("photo", "original");
        attachment.Sha256.ToByteArray().Should().Equal(SHA256.HashData("test"u8));
        var download = await _client.GetAttachmentUrlAsync(new Proto.GetAttachmentUrlRequest
        {
            Parent = parent,
            AttachmentId = attachment.AttachmentId
        }, _headers);
        download.Url.Should().Be("https://downloads.example.test/attachment?signature=test");
        download.Url.Should().NotContain("private/attachment-object");
        download.ExpiresAtUtcMs.Should().BeInRange(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow.AddMinutes(6).ToUnixTimeMilliseconds());
        download.Attachment.Should().Be(attachment);
        await _attachmentMetadata.Received(1).QueryPageAsync(Arg.Is<AttachmentPageQuery>(query =>
            query.LayerId == WebAppFixture.TestLayerId && query.FeatureId == 1 && query.Limit == 101),
            Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/ListAttachments")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ListAttachments")]
    public async Task ListAttachments_GrpcWeb_MissingFeatureNeverReadsAttachmentStore()
    {
        var parent = ExistingAttachmentParent();
        parent.ObjectId = long.MaxValue;
        var call = async () => await _client!.ListAttachmentsAsync(new Proto.ListAttachmentsRequest { Parent = parent }, _headers);
        (await call.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);
        await _attachmentMetadata.DidNotReceiveWithAnyArgs().QueryPageAsync(default!);
    }

    [IntegrationTheory]
    [InlineData("ListAttachments")]
    [InlineData("GetAttachmentUrl")]
    [InlineData("UploadAttachment")]
    [InlineData("BeginAttachmentUpload")]
    [InlineData("UploadAttachmentChunk")]
    [InlineData("CommitAttachmentUpload")]
    [InlineData("AbortAttachmentUpload")]
    [InlineData("DeleteAttachment")]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/ListAttachments")]
    [Endpoint("POST /geospatial.v1.FeatureService/GetAttachmentUrl")]
    [Endpoint("POST /geospatial.v1.FeatureService/UploadAttachment")]
    [Endpoint("POST /geospatial.v1.FeatureService/BeginAttachmentUpload")]
    [Endpoint("POST /geospatial.v1.FeatureService/UploadAttachmentChunk")]
    [Endpoint("POST /geospatial.v1.FeatureService/CommitAttachmentUpload")]
    [Endpoint("POST /geospatial.v1.FeatureService/AbortAttachmentUpload")]
    [Endpoint("POST /geospatial.v1.FeatureService/DeleteAttachment")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ListAttachments")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/GetAttachmentUrl")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/UploadAttachment")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/BeginAttachmentUpload")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/UploadAttachmentChunk")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/CommitAttachmentUpload")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/AbortAttachmentUpload")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/DeleteAttachment")]
    public async Task AttachmentMethods_GrpcWeb_RejectEmptyRequests(string method)
    {
        var call = async () =>
        {
            switch (method)
            {
                case "ListAttachments": await _client!.ListAttachmentsAsync(new Proto.ListAttachmentsRequest(), _headers); break;
                case "GetAttachmentUrl": await _client!.GetAttachmentUrlAsync(new Proto.GetAttachmentUrlRequest(), _headers); break;
                case "UploadAttachment": await _client!.UploadAttachmentAsync(new Proto.UploadAttachmentRequest(), _headers); break;
                case "BeginAttachmentUpload": await _client!.BeginAttachmentUploadAsync(new Proto.BeginAttachmentUploadRequest(), _headers); break;
                case "UploadAttachmentChunk": await _client!.UploadAttachmentChunkAsync(new Proto.UploadAttachmentChunkRequest(), _headers); break;
                case "CommitAttachmentUpload": await _client!.CommitAttachmentUploadAsync(new Proto.CommitAttachmentUploadRequest(), _headers); break;
                case "AbortAttachmentUpload": await _client!.AbortAttachmentUploadAsync(new Proto.AbortAttachmentUploadRequest(), _headers); break;
                case "DeleteAttachment": await _client!.DeleteAttachmentAsync(new Proto.DeleteAttachmentRequest(), _headers); break;
                default: throw new ArgumentOutOfRangeException(nameof(method));
            }
        };
        (await call.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    private static Proto.AttachmentParent ExistingAttachmentParent()
        => new() { ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId, ObjectId = 1 };
}
