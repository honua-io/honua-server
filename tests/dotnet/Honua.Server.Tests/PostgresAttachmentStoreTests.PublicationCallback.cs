// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;
using Honua.Db.Postgres.Features.Attachments;
using Honua.Server.Tests.Infrastructure;

namespace Honua.Server.Tests;

public partial class PostgresAttachmentStoreTests
{
    [Fact]
    public async Task CommitSession_ConcurrentPublicationCallbacks_InvokesPublisherOnce()
    {
        var store = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(store, CreateSessionRequest());
        var calls = 0;
        Task<string> Publish(AttachmentUploadSession session, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            Assert.Equal(key.SessionId, session.SessionId);
            return Task.FromResult(AttachmentUploadObjectKey.ForSession(session.SessionId));
        }
        var results = await Task.WhenAll(store.CommitAsync(key, Publish), NewUploadSessionStore().CommitAsync(key, Publish));
        Assert.Equal(1, calls);
        Assert.Equal(results[0], results[1]);
    }

    [Fact]
    public async Task CommitSession_PublisherFailure_RetainsStagingForRetry()
    {
        var store = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(store, CreateSessionRequest());
        await Assert.ThrowsAsync<IOException>(() => store.CommitAsync(key,
            (_, _) => Task.FromException<string>(new IOException("Publication unavailable."))));
        Assert.Equal(AttachmentUploadState.Uploading, (await store.GetAsync(key)).State);
        Assert.Empty(await _attachmentStore.ListAsync(TestLayerId, TestFeatureId));
        await using var content = new MemoryStream();
        await store.CopyContentAsync(key, content);
        Assert.Equal("abcdef"u8.ToArray(), content.ToArray());
        var published = await store.CommitAsync(key, (session, _) => Task.FromResult(AttachmentUploadObjectKey.ForSession(session.SessionId)));
        Assert.Equal(published, Assert.Single(await _attachmentStore.ListAsync(TestLayerId, TestFeatureId)));
    }

    [Fact]
    public async Task AttachmentMaintenance_ExpiredUncommittedPublication_RemovesUnreferencedBlob()
    {
        var store = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(store, CreateSessionRequest());
        var objectKey = AttachmentUploadObjectKey.ForSession(key.SessionId);
        await using var content = new MemoryStream("abcdef"u8.ToArray());
        var uploaded = await _fileStorage.UploadIfMatchAsync(new Honua.Core.Features.Infrastructure.Domain.FileUploadRequest
        {
            Content = content, FileName = "notes.txt", ContentType = "text/plain", ObjectKeyOverride = objectKey
        }, null);
        Assert.True(uploaded.Success);
        await _fixture.ExecuteAsync($"""
            UPDATE "{_schemaName}".attachment_upload_sessions
            SET created_at = clock_timestamp() - interval '2 hours', expires_at = clock_timestamp() - interval '1 hour'
            WHERE session_id = '{key.SessionId}';
            """);
        var maintenance = new PostgresAttachmentMaintenance(
            new TestDatabaseConnectionProvider(_fixture.DataSource), _schemaName, _attachmentStore);
        await maintenance.RunOnceAsync();
        Assert.False(await _fileStorage.ExistsAsync(objectKey));
        await AssertMaintenanceQueuesEmptyAsync(key.SessionId);
    }
    [Fact]
    public async Task AttachmentMaintenance_LateUploadAfterExpiry_IsRemovedOnLaterCleanupPass()
    {
        var store = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(store, CreateSessionRequest());
        var objectKey = AttachmentUploadObjectKey.ForSession(key.SessionId);
        await _fixture.ExecuteAsync($"""
            UPDATE "{_schemaName}".attachment_upload_sessions
            SET created_at = clock_timestamp() - interval '2 hours', expires_at = clock_timestamp() - interval '1 hour'
            WHERE session_id = '{key.SessionId}';
            """);
        var maintenance = new PostgresAttachmentMaintenance(
            new TestDatabaseConnectionProvider(_fixture.DataSource), _schemaName, _attachmentStore);
        await maintenance.RunOnceAsync();
        await using var content = new MemoryStream("abcdef"u8.ToArray());
        var uploaded = await _fileStorage.UploadIfMatchAsync(new Honua.Core.Features.Infrastructure.Domain.FileUploadRequest
        {
            Content = content, FileName = "notes.txt", ContentType = "text/plain", ObjectKeyOverride = objectKey
        }, null);
        Assert.True(uploaded.Success);
        await _fixture.ExecuteAsync($"""
            UPDATE "{_schemaName}".attachment_upload_cleanup_ledger
            SET next_attempt_at = clock_timestamp() - interval '1 second'
            WHERE session_id = '{key.SessionId}';
            """);
        await maintenance.RunOnceAsync();
        Assert.False(await _fileStorage.ExistsAsync(objectKey));
        await AssertMaintenanceQueuesEmptyAsync(key.SessionId);
    }

}
