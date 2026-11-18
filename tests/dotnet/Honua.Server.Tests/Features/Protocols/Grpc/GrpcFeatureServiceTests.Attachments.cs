// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Protocols.Grpc;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcFeatureServiceTests
{
    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ListAttachments")]
    public async Task ListAttachments_FiltersAndPagesNativeDescriptors()
    {
        ArrangeAttachmentParent();
        var metadata = Substitute.For<IAttachmentNativeMetadataStore>();
        var globalId = Guid.NewGuid();
        var items = new[]
        {
            new AttachmentPageItem(Attachment.CreateForUpload(2, 17, 0, "two", "image/png", 4, "private/two"),
                new AttachmentNativeMetadata(globalId, ["photo"], new byte[32])),
            new AttachmentPageItem(Attachment.CreateForUpload(3, 17, 0, "three", "image/png", 4, "private/three"),
                new AttachmentNativeMetadata(Guid.NewGuid(), ["photo"], new byte[32]))
        };
        metadata.QueryPageAsync(Arg.Any<AttachmentPageQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => items.Where(item => item.Attachment.Id > call.Arg<AttachmentPageQuery>().AfterId).ToArray());
        using var context = CreateCallContext(null, null, null, services =>
        {
            services.AddSingleton(metadata);
        });
        var request = new Proto.ListAttachmentsRequest { Parent = AttachmentParent(), PageSize = 1 };
        request.ContentTypes.Add("image/png");
        request.Keywords.Add("photo");
        var first = await _sut.ListAttachments(request, context);
        first.Attachments.Should().ContainSingle().Which.GlobalId.Should().Be(globalId.ToString("D"));
        first.NextPageToken.Should().NotBeEmpty();
        await metadata.Received(1).QueryPageAsync(Arg.Is<AttachmentPageQuery>(query =>
            query.LayerId == 0 && query.FeatureId == 17 && query.Limit == 2 && query.AfterId == 0 &&
            query.ContentTypes.SequenceEqual(new[] { "image/png" }) && query.Keywords.SequenceEqual(new[] { "photo" })),
            Arg.Any<CancellationToken>());

        request.PageToken = first.NextPageToken;
        var second = await _sut.ListAttachments(request, context);
        second.Attachments.Should().ContainSingle().Which.AttachmentId.Should().Be(3);
        second.NextPageToken.Should().BeEmpty();
        request.Parent.ObjectId = 18;
        var invalid = () => _sut.ListAttachments(request, context);
        (await invalid.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/GetAttachmentUrl")]
    public async Task GetAttachmentUrl_InsecureStorageUrl_IsRefusedWithoutThePath()
    {
        using var context = AttachmentUrlContext("http://storage.internal/private/two");
        var action = () => _sut.GetAttachmentUrl(new Proto.GetAttachmentUrlRequest
        {
            Parent = AttachmentParent(),
            AttachmentId = 2
        }, context);
        var exception = (await action.Should().ThrowAsync<RpcException>()).Which;
        exception.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        exception.Status.Detail.Should().NotContain("private/two");
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/GetAttachmentUrl")]
    public async Task GetAttachmentUrl_LocalStoragePath_ReturnsSignedLinkInsteadOfThePath()
    {
        using var context = AttachmentUrlContext("file:///private/two");
        var response = await _sut.GetAttachmentUrl(new Proto.GetAttachmentUrlRequest
        {
            Parent = AttachmentParent(),
            AttachmentId = 2
        }, context);
        response.Url.Should().StartWith("https://maps.example.test/api/v1/attachments/downloads/");
        response.Url.Should().NotContain("private");
    }

    private TestServerCallContext AttachmentUrlContext(string presignedUrl)
    {
        ArrangeAttachmentParent();
        var store = Substitute.For<IAttachmentStore>();
        store.GetAsync(0, 17, 2, Arg.Any<CancellationToken>())
            .Returns(Attachment.CreateForUpload(2, 17, 0, "two", "image/png", 4, "private/two"));
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetPresignedUrlAsync("private/two", Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(presignedUrl);
        var metadata = Substitute.For<IAttachmentNativeMetadataStore>();
        metadata.GetNativeMetadataAsync(Arg.Any<AttachmentNativeMetadataQuery>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, AttachmentNativeMetadata> { [2] = new(Guid.NewGuid(), [], new byte[32]) });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Public:BaseUrl"] = "https://maps.example.test" })
            .Build();
        return CreateCallContext(null, null, null, services =>
        {
            services.AddSingleton(store);
            services.AddSingleton(storage);
            services.AddSingleton(metadata);
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton(new AttachmentDownloadLinks(new EphemeralDataProtectionProvider()));
        });
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/UploadAttachment")]
    public async Task UploadAttachment_DigestMismatch_DoesNotBeginSession()
    {
        var action = () => _sut.UploadAttachment(new Proto.UploadAttachmentRequest
        {
            Upload = new Proto.AttachmentUpload { Parent = AttachmentParent(), Sha256 = Google.Protobuf.ByteString.CopyFrom(new byte[32]) }
        }, CreateCallContext());
        (await action.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/CommitAttachmentUpload")]
    public async Task CommitAttachmentUpload_InvalidSessionId_RejectsInvalidArgument()
    {
        var action = () => _sut.CommitAttachmentUpload(new Proto.CommitAttachmentUploadRequest(), CreateCallContext());
        (await action.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/AbortAttachmentUpload")]
    public async Task AbortAttachmentUpload_InvalidSessionId_RejectsInvalidArgument()
    {
        var action = () => _sut.AbortAttachmentUpload(new Proto.AbortAttachmentUploadRequest(), CreateCallContext());
        (await action.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/DeleteAttachment")]
    public async Task DeleteAttachment_MissingIdempotencyKey_RejectsInvalidArgument()
    {
        var action = () => _sut.DeleteAttachment(new Proto.DeleteAttachmentRequest
        {
            Parent = AttachmentParent(),
            AttachmentId = 2
        }, CreateCallContext());
        (await action.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    private void ArrangeAttachmentParent()
        => _featureReader.QueryAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, [Feature.Create(17, null)]));

    private static Proto.AttachmentParent AttachmentParent()
        => new() { ServiceId = "test", LayerId = 0, ObjectId = 17 };

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ListAttachments")]
    public async Task ListAttachments_HiddenParent_DoesNotReadAttachmentStore()
    {
        _featureReader.QueryAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(0, []));
        var store = Substitute.For<IAttachmentStore>();
        using var context = CreateCallContext(null, null, null, services => services.AddSingleton(store));
        var action = () => _sut.ListAttachments(new Proto.ListAttachmentsRequest
        {
            Parent = new Proto.AttachmentParent { ServiceId = "test", LayerId = 0, ObjectId = 17 }
        }, context);
        (await action.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);
        await store.DidNotReceiveWithAnyArgs().ListAsync(default, default);
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/BeginAttachmentUpload")]
    public async Task BeginAttachmentUpload_MissingMetadata_RejectsInvalidArgument()
    {
        var action = () => _sut.BeginAttachmentUpload(new Proto.BeginAttachmentUploadRequest(), CreateCallContext());
        (await action.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/UploadAttachmentChunk")]
    public async Task UploadAttachmentChunk_InvalidSessionId_RejectsInvalidArgument()
    {
        var action = () => _sut.UploadAttachmentChunk(new Proto.UploadAttachmentChunkRequest
        {
            SessionId = "invalid"
        }, CreateCallContext());
        (await action.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }
}
