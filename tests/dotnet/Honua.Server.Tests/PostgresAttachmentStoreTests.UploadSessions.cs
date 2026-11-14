// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Core.Features.Attachments.Domain;
using Honua.Db.Postgres.Features.Attachments;
using Honua.Server.Tests.Infrastructure;

namespace Honua.Server.Tests;

public partial class PostgresAttachmentStoreTests
{
    [Fact]
    public async Task CommitSession_ConcurrentRetryAfterRestart_PublishesExactlyOnce()
    {
        var store = await CreateUploadSessionStoreAsync();
        var session = await store.BeginAsync(CreateSessionRequest());
        var key = new AttachmentUploadSessionKey("owner", session.SessionId);
        var content = "abcdef"u8.ToArray();
        await store.AppendAsync(new AttachmentUploadChunk
        {
            Key = key, OffsetBytes = 0, Content = content, Sha256 = SHA256.HashData(content)
        });
        var restarted = NewUploadSessionStore();
        var results = await Task.WhenAll(store.CommitAsync(key, "prepared-object"), restarted.CommitAsync(key, "prepared-object"));
        Assert.Equal(results[0], results[1]);
        Assert.Single(await _attachmentStore.ListAsync(TestLayerId, TestFeatureId));
        await restarted.AbortAsync(key);
        Assert.Equal(results[0], await restarted.CommitAsync(key, "ignored-retry-object"));
        Assert.NotNull(await _attachmentStore.GetAsync(TestLayerId, TestFeatureId, results[0].Id));
    }

    [Fact]
    public async Task CommitSession_InvalidWholeFileDigest_PublishesNoAttachmentOrReceipt()
    {
        var store = await CreateUploadSessionStoreAsync();
        var session = await store.BeginAsync(CreateSessionRequest() with { Sha256 = SHA256.HashData("wrong!"u8) });
        var key = new AttachmentUploadSessionKey("owner", session.SessionId);
        var content = "abcdef"u8.ToArray();
        await store.AppendAsync(new AttachmentUploadChunk
        {
            Key = key, OffsetBytes = 0, Content = content, Sha256 = SHA256.HashData(content)
        });
        var failure = await Assert.ThrowsAsync<AttachmentUploadException>(() => store.CommitAsync(key, "prepared-object"));
        Assert.Equal(AttachmentUploadFailure.InvalidContent, failure.Failure);
        Assert.Empty(await _attachmentStore.ListAsync(TestLayerId, TestFeatureId));
        Assert.Equal(AttachmentUploadState.Uploading, (await store.GetAsync(key)).State);
    }

    [Fact]
    public async Task CommitSession_ReceiptFailure_RollsBackAttachmentAndRetainsChunks()
    {
        var store = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(store, CreateSessionRequest());
        await _fixture.ExecuteAsync($"""
            ALTER TABLE "{_schemaName}".attachment_upload_sessions
            ADD CONSTRAINT reject_commits CHECK (state <> 'committed');
            """);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => store.CommitAsync(key, "prepared-object"));
        Assert.Empty(await _attachmentStore.ListAsync(TestLayerId, TestFeatureId));
        Assert.Equal(AttachmentUploadState.Uploading, (await store.GetAsync(key)).State);
        await _fixture.ExecuteAsync($"ALTER TABLE \"{_schemaName}\".attachment_upload_sessions DROP CONSTRAINT reject_commits;");
        var committed = await store.CommitAsync(key, "prepared-object");
        Assert.Equal(committed, Assert.Single(await _attachmentStore.ListAsync(TestLayerId, TestFeatureId)));
    }

    [Fact]
    public async Task CommitSession_Replacement_KeepsIdentityAndQueuesRetiredBlob()
    {
        var store = await CreateUploadSessionStoreAsync();
        var firstKey = await PrepareCommitSessionAsync(store, CreateSessionRequest());
        var original = await store.CommitAsync(firstKey, "original-object");
        var replacementKey = await PrepareCommitSessionAsync(store, CreateSessionRequest() with
        {
            IdempotencyKey = "replacement", ReplaceAttachmentId = original.Id, Filename = "replacement.txt"
        });
        var replacement = await store.CommitAsync(replacementKey, "replacement-object");
        Assert.Equal(original.Id, replacement.Id);
        Assert.Equal("replacement-object", replacement.StoragePath);
        Assert.Equal(replacement, Assert.Single(await _attachmentStore.ListAsync(TestLayerId, TestFeatureId)));
        Assert.Equal(original, await store.CommitAsync(firstKey, "ignored-object"));
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var cleanup = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM \"{_schemaName}\".import_attachment_cleanup WHERE storage_path = 'original-object'", connection);
        Assert.Equal(1L, await cleanup.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CommitSession_ConcurrentDistinctSessions_EnforcesSharedQuota()
    {
        await CreateUploadSessionStoreAsync();
        var publisher = new PostgresAttachmentStore(
            new TestDatabaseConnectionProvider(_fixture.DataSource), _fileStorage,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresAttachmentStore>.Instance, _schemaName,
            attachmentLimits: new Honua.Core.Configuration.AttachmentLimits
            {
                MaxAttachmentsPerFeature = 1, MaxAttachmentSize = 1024, MaxTotalAttachmentSize = 1024
            }, migrationContract: Honua.Server.Startup.ServerCoreSchemaMigrations.Manifest.Attachments!);
        var store = new PostgresAttachmentUploadSessionStore(new TestDatabaseConnectionProvider(_fixture.DataSource), _schemaName, publisher);
        var keys = await Task.WhenAll(Enumerable.Range(0, 2).Select(index =>
            PrepareCommitSessionAsync(store, CreateSessionRequest() with { IdempotencyKey = $"quota-{index}" })));
        var outcomes = await Task.WhenAll(keys.Select(async key =>
        {
            try
            {
                await store.CommitAsync(key, $"object-{key.SessionId}");
                return true;
            }
            catch (Honua.Core.Exceptions.ValidationException)
            {
                return false;
            }
        }));
        Assert.Equal(1, outcomes.Count(success => success));
        Assert.Single(await publisher.ListAsync(TestLayerId, TestFeatureId));
    }

    private static async Task<AttachmentUploadSessionKey> PrepareCommitSessionAsync(PostgresAttachmentUploadSessionStore store, AttachmentUploadRequest request)
    {
        var session = await store.BeginAsync(request);
        var key = new AttachmentUploadSessionKey(request.OwnerId, session.SessionId);
        var content = "abcdef"u8.ToArray();
        await store.AppendAsync(new AttachmentUploadChunk
        {
            Key = key, OffsetBytes = 0, Content = content, Sha256 = SHA256.HashData(content)
        });
        return key;
    }

    private async Task<PostgresAttachmentUploadSessionStore> CreateUploadSessionStoreAsync()
    {
        var assembly = typeof(Program).Assembly;
        foreach (var filename in new[] { "162_CreateAttachmentUploadSessions.sql", "163_CreateAttachmentNativeMetadata.sql", "164_CreateAttachmentUploadCleanupLedger.sql" })
        {
            var name = Assert.Single(assembly.GetManifestResourceNames(), name => name.EndsWith(filename, StringComparison.Ordinal));
            await using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var sql = (await reader.ReadToEndAsync()).Replace("$HonuaSchema$", $"\"{_schemaName}\"", StringComparison.Ordinal);
            await _fixture.ExecuteAsync(sql);
        }
        return NewUploadSessionStore();
    }

    private PostgresAttachmentUploadSessionStore NewUploadSessionStore()
        => new(new TestDatabaseConnectionProvider(_fixture.DataSource), _schemaName, _attachmentStore);

    private static AttachmentUploadRequest CreateSessionRequest() => new()
    {
        OwnerId = "owner", ServiceId = "service", LayerId = TestLayerId, FeatureId = TestFeatureId,
        Operation = "chunked", IdempotencyKey = "commit-retry", Filename = "notes.txt", ContentType = "text/plain",
        ExpectedSize = 6, Sha256 = SHA256.HashData("abcdef"u8), MaxChunkBytes = 6, Lifetime = TimeSpan.FromHours(1)
    };
}
