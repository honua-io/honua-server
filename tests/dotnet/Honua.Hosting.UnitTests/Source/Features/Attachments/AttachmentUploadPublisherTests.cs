// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Attachments;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Attachments;

public sealed class AttachmentUploadPublisherTests
{
    [UnitTest]
    public async Task PublishAsync_ConditionalCreateFails_ReusesMatchingContent()
    {
        var (publisher, session, cloud, _) = Create("field note"u8.ToArray());
        cloud.DownloadAsync(AttachmentUploadObjectKey.ForSession(session.SessionId), default)
            .Returns(Task.FromResult<Stream?>(new MemoryStream("field note"u8.ToArray())));

        var attachment = await publisher.PublishAsync(session);

        attachment.StoragePath.Should().Be(AttachmentUploadObjectKey.ForSession(session.SessionId));
        await cloud.DidNotReceiveWithAnyArgs().UploadAsync(default(FileUploadRequest)!);
        await cloud.DidNotReceiveWithAnyArgs().DeleteAsync(default!);
    }

    [UnitTest]
    public async Task PublishAsync_ExistingContentDiffers_RejectsWithoutOverwriteOrDelete()
    {
        var (publisher, session, cloud, _) = Create("field note"u8.ToArray());
        cloud.DownloadAsync(AttachmentUploadObjectKey.ForSession(session.SessionId), default)
            .Returns(Task.FromResult<Stream?>(new MemoryStream("wrong note"u8.ToArray())));

        var action = () => publisher.PublishAsync(session);

        await action.Should().ThrowAsync<AttachmentUploadException>();
        await cloud.DidNotReceiveWithAnyArgs().UploadAsync(default(FileUploadRequest)!);
        await cloud.DidNotReceiveWithAnyArgs().DeleteAsync(default!);
    }

    [UnitTest]
    public async Task PublishAsync_ExistingContentTooLarge_StopsAfterExpectedLength()
    {
        var (publisher, session, cloud, _) = Create("field note"u8.ToArray());
        cloud.DownloadAsync(AttachmentUploadObjectKey.ForSession(session.SessionId), default)
            .Returns(Task.FromResult<Stream?>(new MemoryStream(new byte[100000])));

        var action = () => publisher.PublishAsync(session);

        await action.Should().ThrowAsync<AttachmentUploadException>();
    }

    [UnitTest]
    public async Task PublishAsync_ConditionalCreateSucceeds_DoesNotDownloadOrOverwrite()
    {
        var (publisher, session, cloud, _) = Create("field note"u8.ToArray());
        var path = AttachmentUploadObjectKey.ForSession(session.SessionId);
        cloud.UploadIfMatchAsync(default!, default, default).ReturnsForAnyArgs(UploadResult.CreateSuccess(new CloudFile
        {
            FileId = path, FileName = "note.txt", StoragePath = path, ContentType = "text/plain",
            SizeBytes = 10, UploadedAt = DateTimeOffset.UtcNow, Provider = CloudStorageProvider.Local
        }));

        var attachment = await publisher.PublishAsync(session);

        attachment.StoragePath.Should().Be(path);
        await cloud.DidNotReceiveWithAnyArgs().DownloadAsync(default!);
        await cloud.DidNotReceiveWithAnyArgs().UploadAsync(default(FileUploadRequest)!);
    }

    [UnitTest]
    public async Task PublishAsync_CommittedRetry_ReturnsReceiptWithoutReadingOrUploading()
    {
        var (publisher, session, cloud, store) = Create("field note"u8.ToArray());
        var receipt = Attachment.CreateForUpload(1, 2, 1, "note.txt", "text/plain", 10, "stored");
        store.CommitAsync(default, default(Func<AttachmentUploadSession, CancellationToken, Task<string>>)!, default)
            .ReturnsForAnyArgs(Task.FromResult(receipt));

        var attachment = await publisher.PublishAsync(session with { State = AttachmentUploadState.Committed });

        attachment.Should().Be(receipt);
        await store.DidNotReceiveWithAnyArgs().CopyContentAsync(default, default!);
        await cloud.DidNotReceiveWithAnyArgs().UploadIfMatchAsync(default!, default);
    }

    private static (AttachmentUploadPublisher Publisher, AttachmentUploadSession Session, ICloudFileStorage Cloud, IAttachmentUploadSessionStore Sessions) Create(byte[] bytes)
    {
        var store = Substitute.For<IAttachmentUploadSessionStore>();
        store.CopyContentAsync(default, default!, default).ReturnsForAnyArgs(async call =>
            await call.Arg<Stream>().WriteAsync(bytes));
        var session = new AttachmentUploadSession
        {
            SessionId = Guid.NewGuid(), State = AttachmentUploadState.Uploading,
            NextOffsetBytes = bytes.Length, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Request = new AttachmentUploadRequest
            {
                OwnerId = "editor", ServiceId = "service", LayerId = 1, FeatureId = 2,
                Operation = "chunked", IdempotencyKey = "key", Filename = "note.txt", ContentType = "text/plain",
                ExpectedSize = bytes.Length, Sha256 = SHA256.HashData(bytes), MaxChunkBytes = 1024,
                Lifetime = TimeSpan.FromMinutes(5)
            }
        };
        store.CommitAsync(default, default(Func<AttachmentUploadSession, CancellationToken, Task<string>>)!, default)
            .ReturnsForAnyArgs(async call =>
            {
                var path = await call.Arg<Func<AttachmentUploadSession, CancellationToken, Task<string>>>()(session, default);
                return Attachment.CreateForUpload(1, 2, 1, "note.txt", "text/plain", bytes.Length, path);
            });
        var cloud = Substitute.For<ICloudFileStorage>();
        cloud.UploadIfMatchAsync(default!, default, default).ReturnsForAnyArgs(UploadResult.CreateFailure("Conflict"));
        var verifier = new AttachmentUploadContentVerifier(store, Options.Create(new FileUploadSecurityOptions()));
        return (new AttachmentUploadPublisher(store, verifier, cloud), session, cloud, store);
    }
}
