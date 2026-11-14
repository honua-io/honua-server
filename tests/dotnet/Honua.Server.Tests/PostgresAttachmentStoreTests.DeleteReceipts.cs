// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;
using Honua.Db.Postgres.Features.Attachments;
using Honua.Server.Tests.Infrastructure;

namespace Honua.Server.Tests;

public partial class PostgresAttachmentStoreTests
{
    [Fact]
    public async Task DeleteReceipt_ConcurrentRetryAfterRestart_DeletesOnceAndQueuesBlob()
    {
        var sessions = await CreateUploadSessionStoreAsync();
        var attachment = await sessions.CommitAsync(await PrepareCommitSessionAsync(sessions, CreateSessionRequest()), "delete-object");
        var request = CreateDeleteRequest(attachment.Id);
        await Task.WhenAll(NewMutationStore().DeleteAsync(request), NewMutationStore().DeleteAsync(request));
        Assert.Empty(await _attachmentStore.ListAsync(TestLayerId, TestFeatureId));
        await NewMutationStore().DeleteAsync(request);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var count = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM \"{_schemaName}\".import_attachment_cleanup WHERE storage_path = 'delete-object'", connection);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
        var conflict = await Assert.ThrowsAsync<AttachmentUploadException>(() => NewMutationStore().DeleteAsync(request with { AttachmentId = attachment.Id + 1 }));
        Assert.Equal(AttachmentUploadFailure.Conflict, conflict.Failure);
        var otherOwner = await Assert.ThrowsAsync<AttachmentUploadException>(() => NewMutationStore().DeleteAsync(request with { OwnerId = "other" }));
        Assert.Equal(AttachmentUploadFailure.NotFound, otherOwner.Failure);
    }

    [Fact]
    public async Task DeleteReceipt_ReceiptFailure_RollsBackDeleteAndCleanup()
    {
        var sessions = await CreateUploadSessionStoreAsync();
        var attachment = await sessions.CommitAsync(await PrepareCommitSessionAsync(sessions, CreateSessionRequest()), "rollback-object");
        var request = CreateDeleteRequest(attachment.Id);
        await _fixture.ExecuteAsync($"ALTER TABLE \"{_schemaName}\".attachment_mutation_receipts ADD CONSTRAINT reject_receipts CHECK (false);");
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => NewMutationStore().DeleteAsync(request));
        Assert.NotNull(await _attachmentStore.GetAsync(TestLayerId, TestFeatureId, attachment.Id));
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var count = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM \"{_schemaName}\".import_attachment_cleanup", connection);
        Assert.Equal(0L, await count.ExecuteScalarAsync());
        await _fixture.ExecuteAsync($"ALTER TABLE \"{_schemaName}\".attachment_mutation_receipts DROP CONSTRAINT reject_receipts;");
        await NewMutationStore().DeleteAsync(request);
        Assert.Empty(await _attachmentStore.ListAsync(TestLayerId, TestFeatureId));
    }

    private PostgresAttachmentMutationStore NewMutationStore() => new(
        new TestDatabaseConnectionProvider(_fixture.DataSource), _schemaName, _attachmentStore);

    private static AttachmentDeleteRequest CreateDeleteRequest(long attachmentId) => new()
    {
        OwnerId = "owner", ServiceId = "service", LayerId = TestLayerId, FeatureId = TestFeatureId,
        AttachmentId = attachmentId, IdempotencyKey = "delete-retry", Lifetime = TimeSpan.FromHours(1)
    };
}
