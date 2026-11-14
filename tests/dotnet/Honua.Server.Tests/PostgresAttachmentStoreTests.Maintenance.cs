// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Honua.Db.Postgres.Features.Attachments;
using Honua.Server.Tests.Infrastructure;

namespace Honua.Server.Tests;

public partial class PostgresAttachmentStoreTests
{
    [Fact]
    public async Task AttachmentMaintenance_ExpiresStagingAndDrainsRetiredBlobs_WithoutRemovingLiveContent()
    {
        var store = await CreateUploadSessionStoreAsync();
        var expired = await PrepareCommitSessionAsync(store, CreateSessionRequest());
        var original = await CreateTestAttachment();
        var path = await UploadMaintenanceReplacementAsync();
        var replacement = await PrepareCommitSessionAsync(store, CreateSessionRequest() with
        {
            IdempotencyKey = "replacement", ReplaceAttachmentId = original.Id
        });
        var current = await store.CommitAsync(replacement, path);
        await _fixture.ExecuteAsync($"""
            UPDATE "{_schemaName}".attachment_upload_sessions
            SET created_at = clock_timestamp() - interval '2 hours',
                expires_at = clock_timestamp() - interval '1 hour'
            WHERE session_id = '{expired.SessionId}';
            INSERT INTO "{_schemaName}".import_attachment_cleanup (storage_path, layer_id, feature_id)
            SELECT storage_path, layer_id, feature_id FROM "{_schemaName}".attachments WHERE id = {current.Id}
            ON CONFLICT (storage_path) DO NOTHING;
            """);
        var maintenance = new PostgresAttachmentMaintenance(
            new TestDatabaseConnectionProvider(_fixture.DataSource), _schemaName, _attachmentStore);
        await maintenance.RunOnceAsync();
        var missing = await Assert.ThrowsAsync<AttachmentUploadException>(() => store.GetAsync(expired));
        Assert.Equal(AttachmentUploadFailure.NotFound, missing.Failure);
        Assert.Equal(AttachmentUploadState.Committed, (await store.GetAsync(replacement)).State);
        Assert.Equal(current, await _attachmentStore.GetAsync(TestLayerId, TestFeatureId, current.Id));
        Assert.False(await _fileStorage.ExistsAsync(original.StoragePath));
        Assert.True(await _fileStorage.ExistsAsync(current.StoragePath));
        await AssertMaintenanceQueuesEmptyAsync(expired.SessionId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AttachmentMaintenance_DeleteReturnsFalse_RetainsOnlyExistingObjects(bool exists)
    {
        const string path = "retired-object";
        var storage = Substitute.For<ICloudFileStorage>();
        storage.ExistsAsync(path, Arg.Any<CancellationToken>()).Returns(exists);
        var store = new PostgresAttachmentStore(
            new TestDatabaseConnectionProvider(_fixture.DataSource), storage,
            NullLogger<PostgresAttachmentStore>.Instance, _schemaName,
            migrationContract: Honua.Server.Startup.ServerCoreSchemaMigrations.Manifest.Attachments!);
        await _fixture.ExecuteAsync($"""
            INSERT INTO "{_schemaName}".import_attachment_cleanup (storage_path, layer_id, feature_id)
            VALUES ('{path}', {TestLayerId}, {TestFeatureId});
            """);
        await store.DrainPendingAttachmentCleanupAsync(CancellationToken.None);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var count = new Npgsql.NpgsqlCommand(
            $"SELECT count(*) FROM \"{_schemaName}\".import_attachment_cleanup", connection);
        Assert.Equal(exists ? 1L : 0L, await count.ExecuteScalarAsync());
        if (exists)
        {
            storage.DeleteAsync(path, Arg.Any<CancellationToken>()).Returns(true);
            await store.DrainPendingAttachmentCleanupAsync(CancellationToken.None);
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
    }

    private async Task<string> UploadMaintenanceReplacementAsync()
    {
        var uploaded = await _fileStorage.UploadAsync(new ByteArrayUploadRequest
        {
            Content = "abcdef"u8.ToArray(), FileName = "replacement.txt", ContentType = "text/plain"
        });
        Assert.True(uploaded.Success);
        return Assert.IsType<CloudFile>(uploaded.File).FileId;
    }

    private async Task AssertMaintenanceQueuesEmptyAsync(Guid sessionId)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var remaining = new Npgsql.NpgsqlCommand($"""
            SELECT (SELECT count(*) FROM "{_schemaName}".attachment_upload_chunks WHERE session_id = '{sessionId}'),
                   (SELECT count(*) FROM "{_schemaName}".import_attachment_cleanup)
            """, connection);
        await using var reader = await remaining.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
    }
}
