// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Db.Postgres.Features.Attachments;
using Honua.Server.Tests.Infrastructure;

using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests;

public partial class PostgresAttachmentStoreTests
{
    [IntegrationTest]
    public async Task AttachmentMaintenance_ExpiresStagingAndDrainsRetiredBlobs_WithoutRemovingLiveContent()
    {
        var store = await CreateUploadSessionStoreAsync();
        var expired = await PrepareCommitSessionAsync(store, CreateSessionRequest());
        var original = await CreateTestAttachment();
        var replacement = await PrepareCommitSessionAsync(store, CreateSessionRequest() with
        {
            IdempotencyKey = "replacement",
            ReplaceAttachmentId = original.Id
        });
        var path = await UploadMaintenanceReplacementAsync(replacement.SessionId);
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
        Assert.Equal(current with { NativeMetadata = null }, await _attachmentStore.GetAsync(TestLayerId, TestFeatureId, current.Id));
        Assert.False(await _fileStorage.ExistsAsync(original.StoragePath));
        Assert.True(await _fileStorage.ExistsAsync(current.StoragePath));
        await AssertMaintenanceQueuesEmptyAsync(expired.SessionId);
    }

    [IntegrationTest]
    public async Task AttachmentMaintenance_FailingBatch_DoesNotStarveLaterObjectsAfterRestart()
    {
        await CreateUploadSessionStoreAsync();
        await _fixture.ExecuteAsync($"""
            INSERT INTO "{_schemaName}".import_attachment_cleanup (storage_path, layer_id, feature_id)
            SELECT 'a-failing-' || lpad(i::text, 3, '0'), {TestLayerId}, {TestFeatureId} FROM generate_series(1, 100) i;
            INSERT INTO "{_schemaName}".import_attachment_cleanup (storage_path, layer_id, feature_id)
            VALUES ('z-deletable', {TestLayerId}, {TestFeatureId});
            """);
        var storage = NSubstitute.Substitute.For<Honua.Core.Features.Infrastructure.Abstractions.ICloudFileStorage>();
        storage.DeleteAsync(NSubstitute.Arg.Any<string>(), NSubstitute.Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0).StartsWith("a-failing-", StringComparison.Ordinal)
                ? Task.FromException<bool>(new IOException("Object unavailable.")) : Task.FromResult(true));
        PostgresAttachmentMaintenance NewMaintenance()
        {
            var provider = new TestDatabaseConnectionProvider(_fixture.DataSource);
            var attachments = new PostgresAttachmentStore(provider, storage,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresAttachmentStore>.Instance, _schemaName,
                migrationContract: Honua.Server.Startup.ServerCoreSchemaMigrations.Manifest.Attachments!);
            return new PostgresAttachmentMaintenance(provider, _schemaName, attachments);
        }
        await NewMaintenance().RunOnceAsync();
        await NewMaintenance().RunOnceAsync();
        await storage.Received(1).DeleteAsync("z-deletable", NSubstitute.Arg.Any<CancellationToken>());
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new Npgsql.NpgsqlCommand($"""
            SELECT (SELECT count(*) FROM "{_schemaName}".import_attachment_cleanup),
                   (SELECT count(*) FROM "{_schemaName}".attachment_cleanup_retries)
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(100L, reader.GetInt64(0));
        Assert.Equal(100L, reader.GetInt64(1));
    }

    [IntegrationTest]
    public async Task AttachmentMaintenance_FalseDeletion_RetainsRetryUntilAbsenceConfirmed()
    {
        await CreateUploadSessionStoreAsync();
        await _fixture.ExecuteAsync($"""
            INSERT INTO "{_schemaName}".import_attachment_cleanup (storage_path, layer_id, feature_id)
            VALUES ('still-present', {TestLayerId}, {TestFeatureId}), ('already-absent', {TestLayerId}, {TestFeatureId});
            """);
        var storage = Substitute.For<Honua.Core.Features.Infrastructure.Abstractions.ICloudFileStorage>();
        storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        storage.ExistsAsync("still-present", Arg.Any<CancellationToken>()).Returns(true);
        var provider = new TestDatabaseConnectionProvider(_fixture.DataSource);
        var attachments = new PostgresAttachmentStore(provider, storage,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresAttachmentStore>.Instance, _schemaName,
            migrationContract: Honua.Server.Startup.ServerCoreSchemaMigrations.Manifest.Attachments!);
        await new PostgresAttachmentMaintenance(provider, _schemaName, attachments).RunOnceAsync();
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new Npgsql.NpgsqlCommand($"""
            SELECT c.storage_path FROM "{_schemaName}".import_attachment_cleanup c
            JOIN "{_schemaName}".attachment_cleanup_retries r ON r.storage_path = c.storage_path
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("still-present", reader.GetString(0));
        Assert.False(await reader.ReadAsync());
    }

    private async Task<string> UploadMaintenanceReplacementAsync(Guid sessionId)
    {
        await using var content = new MemoryStream("abcdef"u8.ToArray());
        var uploaded = await _fileStorage.UploadIfMatchAsync(new FileUploadRequest
        {
            Content = content,
            FileName = "replacement.txt",
            ContentType = "text/plain",
            ObjectKeyOverride = AttachmentUploadObjectKey.ForSession(sessionId)
        }, null);
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
