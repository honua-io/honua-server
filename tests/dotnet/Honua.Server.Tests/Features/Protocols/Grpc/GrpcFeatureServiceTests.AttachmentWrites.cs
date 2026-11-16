// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Cryptography;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Honua.Core.Configuration;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcFeatureServiceTests
{
    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/BeginAttachmentUpload")]
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
            value.ServiceId == "test" && value.PublishedLayerId == 7 && value.LayerId == 42 && value.FeatureId == 900 && value.Operation == "chunked"),
            Arg.Any<CancellationToken>());
        await _featureReader.Received(1).QueryAsync(42, Arg.Is<FeatureQuery>(value =>
            value.PublicIdAttributeName == "objectid" && !value.ReturnPublicObjectIds &&
            value.ObjectIds != null && value.ObjectIds.Value.Contains(17)), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/UploadAttachment")]
    public async Task UploadAttachment_PublishesAcceptedChunkAndReturnsNativeDescriptor()
    {
        ArrangeAttachmentEditing();
        var store = Substitute.For<IAttachmentUploadSessionStore>();
        store.BeginAsync(Arg.Any<AttachmentUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(NewAttachmentSession(call.Arg<AttachmentUploadRequest>())));
        ArrangeAcceptedAttachmentChunk(store);
        var publisher = Substitute.For<IAttachmentUploadPublisher>();
        publisher.PublishAsync(Arg.Any<AttachmentUploadSession>(), Arg.Any<CancellationToken>())
            .Returns(Attachment.CreateForUpload(23, 900, 42, "photo.txt", "text/plain", 4, "private/file") with
            {
                NativeMetadata = new AttachmentNativeMetadata(Guid.NewGuid(), ["original"], SHA256.HashData("test"u8))
            });
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
            Upload = ValidAttachmentUpload(),
            Content = ByteString.CopyFromUtf8("test")
        }, context);
        await store.Received(1).BeginAsync(Arg.Is<AttachmentUploadRequest>(value => value.Operation == "unary"),
            Arg.Any<CancellationToken>());
        response.Attachment.AttachmentId.Should().Be(23);
        response.Attachment.Name.Should().Be("photo.txt");
        response.Attachment.Keywords.Should().Equal("original");
        response.Attachment.Sha256.ToByteArray().Should().Equal(SHA256.HashData("test"u8));
        await metadata.DidNotReceiveWithAnyArgs().GetNativeMetadataAsync(default!);
        await publisher.Received(1).PublishAsync(Arg.Is<AttachmentUploadSession>(value => value.NextOffsetBytes == 4 && value.Request.Operation == "unary"),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/CommitAttachmentUpload")]
    public async Task CommitAttachmentUpload_ChangedStorageBinding_DoesNotPublish()
    {
        ArrangeAttachmentEditing();
        var store = Substitute.For<IAttachmentUploadSessionStore>();
        var request = new AttachmentUploadRequest
        {
            OwnerId = "owner",
            ServiceId = "test",
            LayerId = 99,
            PublishedLayerId = 7,
            FeatureId = 900,
            Operation = "chunked",
            IdempotencyKey = "test-upload",
            Filename = "photo.txt",
            ContentType = "text/plain",
            ExpectedSize = 4,
            Sha256 = SHA256.HashData("test"u8),
            MaxChunkBytes = 1024 * 1024,
            Lifetime = TimeSpan.FromHours(24)
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

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/BeginAttachmentUpload")]
    public async Task BeginAttachmentUpload_AllowsParentOwner()
    {
        ArrangeAttachmentEditing("test-user");
        var store = Substitute.For<IAttachmentUploadSessionStore>();
        store.BeginAsync(Arg.Any<AttachmentUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(NewAttachmentSession(call.Arg<AttachmentUploadRequest>())));
        using var context = CreateCallContext(CreateAuthenticatedUser("data-editor"), null, null,
            services => services.AddSingleton(store));
        var response = await _sut.BeginAttachmentUpload(new Proto.BeginAttachmentUploadRequest
        {
            Upload = ValidAttachmentUpload()
        }, context);
        response.Session.Should().NotBeNull();
        await store.Received(1).BeginAsync(Arg.Any<AttachmentUploadRequest>(), Arg.Any<CancellationToken>());
        await AssertAttachmentOwnerQueryAsync();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/BeginAttachmentUpload")]
    public async Task BeginAttachmentUpload_DeniesAnotherParentOwner()
    {
        ArrangeAttachmentEditing("another-user");
        var store = Substitute.For<IAttachmentUploadSessionStore>();
        using var context = CreateCallContext(CreateAuthenticatedUser("data-editor"), null, null,
            services => services.AddSingleton(store));
        var action = () => _sut.BeginAttachmentUpload(new Proto.BeginAttachmentUploadRequest
        {
            Upload = ValidAttachmentUpload()
        }, context);
        (await action.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        await store.DidNotReceiveWithAnyArgs().BeginAsync(default!);
        await AssertAttachmentOwnerQueryAsync();
    }

    private async Task AssertAttachmentOwnerQueryAsync() =>
        await _featureReader.Received(1).QueryAsync(42, Arg.Is<FeatureQuery>(query =>
            query.PublicIdAttributeName == null && query.ObjectIds != null && query.ObjectIds.Value.Contains(900) &&
            query.OutFields != null && query.OutFields.Value.Contains("owner") &&
            query.EnforcedMaskedFields != null && query.EnforcedMaskedFields.Value.IsEmpty), Arg.Any<CancellationToken>());

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/DeleteAttachment")]
    public async Task DeleteAttachment_OwnerPolicyDeniesBeforeMutation()
    {
        ArrangeAttachmentEditing("another-user");
        var store = Substitute.For<IAttachmentMutationStore>();
        using var context = CreateCallContext(CreateAuthenticatedUser("data-editor"), null, null,
            services => services.AddSingleton(store));
        var action = () => _sut.DeleteAttachment(new Proto.DeleteAttachmentRequest
        {
            Parent = ValidAttachmentUpload().Parent,
            AttachmentId = 23,
            IdempotencyKey = "delete"
        }, context);
        (await action.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        await store.DidNotReceiveWithAnyArgs().DeleteAsync(default!);
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/CommitAttachmentUpload")]
    public async Task CommitAttachmentUpload_RechecksParentOwnerBeforePublishing()
    {
        ArrangeAttachmentEditing("another-user");
        var store = Substitute.For<IAttachmentUploadSessionStore>();
        var session = NewAttachmentSession(new AttachmentUploadRequest
        {
            OwnerId = "owner",
            ServiceId = "test",
            LayerId = 42,
            PublishedLayerId = 7,
            FeatureId = 900,
            Operation = "chunked",
            IdempotencyKey = "test-upload",
            Filename = "photo.txt",
            ContentType = "text/plain",
            ExpectedSize = 4,
            Sha256 = SHA256.HashData("test"u8),
            MaxChunkBytes = 1024 * 1024,
            Lifetime = TimeSpan.FromHours(24)
        });
        store.GetAsync(Arg.Any<AttachmentUploadSessionKey>(), Arg.Any<CancellationToken>()).Returns(session);
        var publisher = Substitute.For<IAttachmentUploadPublisher>();
        using var context = CreateCallContext(CreateAuthenticatedUser("data-editor"), null, null, services =>
        {
            services.AddSingleton(store);
            services.AddSingleton(publisher);
        });
        var action = () => _sut.CommitAttachmentUpload(new Proto.CommitAttachmentUploadRequest
        {
            SessionId = session.SessionId.ToString("D")
        }, context);
        (await action.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        await publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!);
    }

    [UnitTheory]
    [InlineData("image/png")]
    [InlineData("TEXT/PLAIN; charset=utf-8")]
    [Endpoint("POST /geospatial.v1.FeatureService/BeginAttachmentUpload")]
    public async Task BeginAttachmentUpload_AcceptsConfiguredMimeTypes(string contentType)
    {
        ArrangeAttachmentEditing();
        var store = Substitute.For<IAttachmentUploadSessionStore>();
        store.BeginAsync(Arg.Any<AttachmentUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(NewAttachmentSession(call.Arg<AttachmentUploadRequest>())));
        using var context = CreateCallContext(null, null, null, services =>
        {
            services.AddSingleton(store);
            services.AddSingleton(Options.Create(new LimitsOptions
            {
                Attachments = new AttachmentLimits { AllowedMimeTypes = "image/*, text/plain" }
            }));
        });
        var upload = ValidAttachmentUpload();
        upload.ContentType = contentType;
        var action = () => _sut.BeginAttachmentUpload(new Proto.BeginAttachmentUploadRequest { Upload = upload }, context);
        (await action()).Session.Should().NotBeNull();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/BeginAttachmentUpload")]
    public async Task BeginAttachmentUpload_RejectsUnconfiguredMimeType()
    {
        ArrangeAttachmentEditing();
        var store = Substitute.For<IAttachmentUploadSessionStore>();
        using var context = CreateCallContext(null, null, null, services =>
        {
            services.AddSingleton(store);
            services.AddSingleton(Options.Create(new LimitsOptions
            {
                Attachments = new AttachmentLimits { AllowedMimeTypes = "image/*, text/plain" }
            }));
        });
        var upload = ValidAttachmentUpload();
        upload.ContentType = "application/octet-stream";
        var action = () => _sut.BeginAttachmentUpload(new Proto.BeginAttachmentUploadRequest { Upload = upload }, context);
        (await action.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        await store.DidNotReceiveWithAnyArgs().BeginAsync(default!);
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/CommitAttachmentUpload")]
    public async Task CommitAttachmentUpload_ChangedMimePolicy_DoesNotPublish()
    {
        ArrangeAttachmentEditing();
        var store = Substitute.For<IAttachmentUploadSessionStore>();
        var session = NewAttachmentSession(new AttachmentUploadRequest
        {
            OwnerId = "owner",
            ServiceId = "test",
            LayerId = 42,
            PublishedLayerId = 7,
            FeatureId = 900,
            Operation = "chunked",
            IdempotencyKey = "test-upload",
            Filename = "photo.txt",
            ContentType = "text/plain",
            ExpectedSize = 4,
            Sha256 = SHA256.HashData("test"u8),
            MaxChunkBytes = 1024 * 1024,
            Lifetime = TimeSpan.FromHours(24)
        });
        store.GetAsync(Arg.Any<AttachmentUploadSessionKey>(), Arg.Any<CancellationToken>()).Returns(session);
        var publisher = Substitute.For<IAttachmentUploadPublisher>();
        using var context = CreateCallContext(null, null, null, services =>
        {
            services.AddSingleton(store);
            services.AddSingleton(publisher);
            services.AddSingleton(Options.Create(new LimitsOptions
            {
                Attachments = new AttachmentLimits { AllowedMimeTypes = "image/*" }
            }));
        });
        var action = () => _sut.CommitAttachmentUpload(new Proto.CommitAttachmentUploadRequest
        {
            SessionId = session.SessionId.ToString("D")
        }, context);
        (await action.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        await publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!);
        await _sut.AbortAttachmentUpload(new Proto.AbortAttachmentUploadRequest
        {
            SessionId = session.SessionId.ToString("D")
        }, context);
        await store.Received(1).AbortAsync(Arg.Any<AttachmentUploadSessionKey>(), Arg.Any<CancellationToken>());
    }

    private static void ArrangeAcceptedAttachmentChunk(IAttachmentUploadSessionStore store)
    {
        store.AppendAsync(Arg.Any<AttachmentUploadChunk>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(NewAttachmentSession(new AttachmentUploadRequest
            {
                OwnerId = call.Arg<AttachmentUploadChunk>().Key.OwnerId,
                ServiceId = "test",
                LayerId = 42,
                PublishedLayerId = 7,
                FeatureId = 900,
                Operation = "unary",
                IdempotencyKey = "test-upload",
                Filename = "photo.txt",
                ContentType = "text/plain",
                ExpectedSize = 4,
                Sha256 = SHA256.HashData("test"u8),
                MaxChunkBytes = 1024 * 1024,
                Lifetime = TimeSpan.FromHours(24)
            }) with
            { NextOffsetBytes = 4 }));
    }

    private void ArrangeAttachmentEditing(string? owner = null)
    {
        var resource = _testResource with
        {
            Editing = new MetadataV2ObjectEditing { SupportsAttachments = true },
            OwnerEditPolicy = owner is null ? null : new MetadataV2OwnerEditPolicy { Enabled = true, OwnerField = "owner" },
            SchemaFields = owner is null ? _testResource.SchemaFields :
                [.. _testResource.SchemaFields, new MetadataV2Field { Name = "owner", Type = MetadataV2FieldType.String }]
        };
        var triple = CreateTriple(_testService, resource);
        triple = triple with
        {
            StorageLayerId = 42,
            Publication = triple.Publication with { Identifier = new MetadataV2LayerIdentifier { Value = "7", IsNumeric = true } }
        };
        _resourceValidator.ValidateServiceLayerV2Async("test", 7, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(triple));
        _featureReader.QueryAsync(42, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, [Feature.Create(900, null, ImmutableDictionary<string, object?>.Empty.Add("owner", owner))]));
    }

    private static Proto.AttachmentUpload ValidAttachmentUpload() => new()
    {
        Parent = new Proto.AttachmentParent { ServiceId = "test", LayerId = 7, ObjectId = 17 },
        Name = "photo.txt",
        ContentType = "text/plain",
        IdempotencyKey = "test-upload",
        SizeBytes = 4,
        Sha256 = ByteString.CopyFrom(SHA256.HashData("test"u8))
    };

    private static AttachmentUploadSession NewAttachmentSession(AttachmentUploadRequest request) => new()
    {
        SessionId = Guid.NewGuid(),
        Request = request,
        NextOffsetBytes = 0,
        State = AttachmentUploadState.Uploading,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
    };
}
