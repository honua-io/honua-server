// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Google.Protobuf;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcFeatureServiceTests
{
    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/UploadAttachment")]
    public async Task UploadAttachment_DuplicateCommitsBeforeAppend_ReturnsCommittedReceipt()
    {
        ArrangeAttachmentEditing();
        var store = Substitute.For<IAttachmentUploadSessionStore>();
        AttachmentUploadSession? session = null;
        var appendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var duplicateCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeginAsync(Arg.Any<AttachmentUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(session = NewAttachmentSession(call.Arg<AttachmentUploadRequest>())));
        store.AppendAsync(Arg.Any<AttachmentUploadChunk>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                appendEntered.SetResult();
                await duplicateCommitted.Task;
                return await Task.FromException<AttachmentUploadSession>(
                    new AttachmentUploadException(AttachmentUploadFailure.InvalidState, "Already committed."));
            });
        store.GetAsync(Arg.Any<AttachmentUploadSessionKey>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(session!));
        var publisher = Substitute.For<IAttachmentUploadPublisher>();
        publisher.PublishAsync(Arg.Any<AttachmentUploadSession>(), Arg.Any<CancellationToken>())
            .Returns(Attachment.CreateForUpload(23, 900, 42, "photo.txt", "text/plain", 4, "private/file") with
            {
                NativeMetadata = new AttachmentNativeMetadata(Guid.NewGuid(), [], ValidAttachmentUpload().Sha256.ToByteArray())
            });
        using var context = CreateCallContext(null, null, null, services =>
        {
            services.AddSingleton(store);
            services.AddSingleton(publisher);
        });
        var pending = _sut.UploadAttachment(new Proto.UploadAttachmentRequest
        {
            Upload = ValidAttachmentUpload(), Content = ByteString.CopyFromUtf8("test")
        }, context);
        await appendEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session = session! with { State = AttachmentUploadState.Committed, NextOffsetBytes = 4 };
        duplicateCommitted.SetResult();

        var response = await pending;

        response.Attachment.AttachmentId.Should().Be(23);
        await store.Received(1).GetAsync(new AttachmentUploadSessionKey(session.Request.OwnerId, session.SessionId),
            Arg.Any<CancellationToken>());
        await publisher.Received(1).PublishAsync(session, Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData(AttachmentUploadState.Uploading)]
    [InlineData(AttachmentUploadState.Aborted)]
    [Endpoint("POST /geospatial.v1.FeatureService/UploadAttachment")]
    public async Task UploadAttachment_AppendRejectedWithoutCommittedReceipt_DoesNotPublish(AttachmentUploadState state)
    {
        ArrangeAttachmentEditing();
        var store = Substitute.For<IAttachmentUploadSessionStore>();
        AttachmentUploadSession? session = null;
        store.BeginAsync(Arg.Any<AttachmentUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(session = NewAttachmentSession(call.Arg<AttachmentUploadRequest>())));
        store.AppendAsync(Arg.Any<AttachmentUploadChunk>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AttachmentUploadSession>(
                new AttachmentUploadException(AttachmentUploadFailure.InvalidState, "Chunk rejected.")));
        store.GetAsync(Arg.Any<AttachmentUploadSessionKey>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(session! with { State = state }));
        var publisher = Substitute.For<IAttachmentUploadPublisher>();
        using var context = CreateCallContext(null, null, null, services =>
        {
            services.AddSingleton(store);
            services.AddSingleton(publisher);
        });
        var action = () => _sut.UploadAttachment(new Proto.UploadAttachmentRequest
        {
            Upload = ValidAttachmentUpload(), Content = ByteString.CopyFromUtf8("test")
        }, context);

        (await action.Should().ThrowAsync<AttachmentUploadException>()).Which.Failure.Should().Be(AttachmentUploadFailure.InvalidState);
        await publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!);
    }
}
