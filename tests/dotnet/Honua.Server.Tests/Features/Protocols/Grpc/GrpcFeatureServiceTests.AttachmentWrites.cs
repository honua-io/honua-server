// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcFeatureServiceTests
{
    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/BeginAttachmentUpload")]
    public async Task BeginAttachmentUpload_BindsPublicLayerToResolvedStorageParent()
    {
        ArrangeAttachmentEditing();
        var store = Substitute.For<IAttachmentUploadSessionStore>();
        store.BeginAsync(Arg.Any<AttachmentUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(NewAttachmentSession(call.Arg<AttachmentUploadRequest>())));
        using var context = CreateCallContext(null, null, null, services => services.AddSingleton(store));
        var response = await _sut.BeginAttachmentUpload(new Proto.BeginAttachmentUploadRequest
        {
            Upload = ValidAttachmentUpload()
        }, context);
        response.Session.MaxChunkBytes.Should().Be(1024 * 1024);
        await store.Received(1).BeginAsync(Arg.Is<AttachmentUploadRequest>(value =>
            value.ServiceId == "test" && value.PublishedLayerId == 7 && value.LayerId == 42 && value.FeatureId == 900),
            Arg.Any<CancellationToken>());
        await _featureReader.Received(1).QueryAsync(42, Arg.Is<FeatureQuery>(value =>
            value.PublicIdAttributeName == "objectid" && !value.ReturnPublicObjectIds &&
            value.ObjectIds != null && value.ObjectIds.Value.Contains(17)), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/UploadAttachment")]
    public async Task UploadAttachment_PublishesAcceptedChunkAndReturnsNativeDescriptor()
    {
        ArrangeAttachmentEditing();
        var store = Substitute.For<IAttachmentUploadSessionStore>();
        store.BeginAsync(Arg.Any<AttachmentUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(NewAttachmentSession(call.Arg<AttachmentUploadRequest>())));
        store.AppendAsync(Arg.Any<AttachmentUploadChunk>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(NewAttachmentSession(new AttachmentUploadRequest
            {
                OwnerId = call.Arg<AttachmentUploadChunk>().Key.OwnerId, ServiceId = "test", LayerId = 42,
                PublishedLayerId = 7, FeatureId = 900, Operation = "create", IdempotencyKey = "test-upload",
                Filename = "photo.txt", ContentType = "text/plain", ExpectedSize = 4,
                Sha256 = SHA256.HashData("test"u8), MaxChunkBytes = 1024 * 1024, Lifetime = TimeSpan.FromHours(24)
            }) with { NextOffsetBytes = 4 }));
        var publisher = Substitute.For<IAttachmentUploadPublisher>();
        publisher.PublishAsync(Arg.Any<AttachmentUploadSession>(), Arg.Any<CancellationToken>())
            .Returns(Attachment.CreateForUpload(23, 900, 42, "photo.txt", "text/plain", 4, "private/file"));
        var metadata = Substitute.For<IAttachmentNativeMetadataStore>();
        metadata.GetNativeMetadataAsync(Arg.Any<AttachmentNativeMetadataQuery>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, AttachmentNativeMetadata>());
        using var context = CreateCallContext(null, null, null, services =>
        {
            services.AddSingleton(store);
            services.AddSingleton(publisher);
            services.AddSingleton(metadata);
        });
        var response = await _sut.UploadAttachment(new Proto.UploadAttachmentRequest
        {
            Upload = ValidAttachmentUpload(), Content = ByteString.CopyFromUtf8("test")
        }, context);
        response.Attachment.AttachmentId.Should().Be(23);
        response.Attachment.Name.Should().Be("photo.txt");
        await publisher.Received(1).PublishAsync(Arg.Is<AttachmentUploadSession>(value => value.NextOffsetBytes == 4),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/CommitAttachmentUpload")]
    public async Task CommitAttachmentUpload_ChangedStorageBinding_DoesNotPublish()
    {
        ArrangeAttachmentEditing();
        var store = Substitute.For<IAttachmentUploadSessionStore>();
        var request = new AttachmentUploadRequest
        {
            OwnerId = "owner", ServiceId = "test", LayerId = 99, PublishedLayerId = 7, FeatureId = 900,
            Operation = "create", IdempotencyKey = "test-upload", Filename = "photo.txt", ContentType = "text/plain",
            ExpectedSize = 4, Sha256 = SHA256.HashData("test"u8), MaxChunkBytes = 1024 * 1024, Lifetime = TimeSpan.FromHours(24)
        };
        var session = NewAttachmentSession(request);
        store.GetAsync(Arg.Any<AttachmentUploadSessionKey>(), Arg.Any<CancellationToken>()).Returns(session);
        var publisher = Substitute.For<IAttachmentUploadPublisher>();
        using var context = CreateCallContext(null, null, null, services =>
        {
            services.AddSingleton(store);
            services.AddSingleton(publisher);
        });
        var action = () => _sut.CommitAttachmentUpload(new Proto.CommitAttachmentUploadRequest
        {
            SessionId = session.SessionId.ToString("D")
        }, context);
        (await action.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        await publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!);
    }

    private void ArrangeAttachmentEditing()
    {
        var resource = _testResource with { Editing = new MetadataV2ResourceEditing { SupportsAttachments = true } };
        var triple = CreateTriple(_testService, resource);
        triple = triple with
        {
            StorageLayerId = 42,
            Publication = triple.Publication with { Identifier = new MetadataV2PublicationIdentifier { Value = "7", IsNumeric = true } }
        };
        _resourceValidator.ValidateServiceLayerV2Async("test", 7, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(triple));
        _featureReader.QueryAsync(42, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, [Feature.Create(900, null)]));
    }

    private static Proto.AttachmentUpload ValidAttachmentUpload() => new()
    {
        Parent = new Proto.AttachmentParent { ServiceId = "test", LayerId = 7, ObjectId = 17 },
        Name = "photo.txt", ContentType = "text/plain", IdempotencyKey = "test-upload", SizeBytes = 4,
        Sha256 = ByteString.CopyFrom(SHA256.HashData("test"u8))
    };

    private static AttachmentUploadSession NewAttachmentSession(AttachmentUploadRequest request) => new()
    {
        SessionId = Guid.NewGuid(), Request = request, NextOffsetBytes = 0,
        State = AttachmentUploadState.Uploading, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
    };
}
