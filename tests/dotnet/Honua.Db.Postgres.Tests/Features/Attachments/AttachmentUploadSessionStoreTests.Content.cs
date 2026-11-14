// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.Attachments.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Attachments;

public sealed partial class AttachmentUploadSessionStoreTests
{
    [IntegrationTest]
    public async Task CopyContentAsync_RestartedStore_ReassemblesVerifiedOrderedBytes()
    {
        await WithStoreAsync(async (store, schema) =>
        {
            var session = await store.BeginAsync(CreateRequest());
            var key = new AttachmentUploadSessionKey("owner", session.SessionId);
            await store.AppendAsync(CreateChunk(key, 0, "abc"u8.ToArray()));
            await store.AppendAsync(CreateChunk(key, 3, "def"u8.ToArray()));
            using var destination = new MemoryStream();
            await CreateStore(schema).CopyContentAsync(key, destination);
            destination.ToArray().Should().Equal("abcdef"u8.ToArray());
            (await store.GetAsync(key)).State.Should().Be(AttachmentUploadState.Uploading);
        });
    }

    [IntegrationTest]
    public async Task CopyContentAsync_IncompleteAbortedOrWrongWholeFileDigest_RejectsContent()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var session = await store.BeginAsync(CreateRequest() with { Sha256 = SHA256.HashData("wrong!"u8) });
            var key = new AttachmentUploadSessionKey("owner", session.SessionId);
            using var destination = new MemoryStream();
            var incomplete = await Assert.ThrowsAsync<AttachmentUploadException>(() => store.CopyContentAsync(key, destination));
            incomplete.Failure.Should().Be(AttachmentUploadFailure.InvalidState);
            destination.Length.Should().Be(0);
            await store.AppendAsync(CreateChunk(key, 0, "abc"u8.ToArray()));
            await store.AppendAsync(CreateChunk(key, 3, "def"u8.ToArray()));
            var mismatch = await Assert.ThrowsAsync<AttachmentUploadException>(() => store.CopyContentAsync(key, destination));
            mismatch.Failure.Should().Be(AttachmentUploadFailure.InvalidContent);
            await store.AbortAsync(key);
            var aborted = await Assert.ThrowsAsync<AttachmentUploadException>(() => store.CopyContentAsync(key, destination));
            aborted.Failure.Should().Be(AttachmentUploadFailure.InvalidState);
        });
    }

    private static AttachmentUploadChunk CreateChunk(AttachmentUploadSessionKey key, long offset, byte[] content)
        => new() { Key = key, OffsetBytes = offset, Content = content, Sha256 = SHA256.HashData(content) };
}
