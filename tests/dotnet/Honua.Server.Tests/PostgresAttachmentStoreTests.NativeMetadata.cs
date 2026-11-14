// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;

namespace Honua.Server.Tests;

public partial class PostgresAttachmentStoreTests
{
    [Fact]
    public async Task NativeMetadata_QueryPage_BoundsAndScopesExactKeywordMatches()
    {
        var sessions = await CreateUploadSessionStoreAsync();
        var first = await sessions.CommitAsync(await PrepareCommitSessionAsync(sessions,
            CreateSessionRequest() with { IdempotencyKey = "first", Keywords = ["a,b"] }), "first-object");
        var second = await sessions.CommitAsync(await PrepareCommitSessionAsync(sessions,
            CreateSessionRequest() with { IdempotencyKey = "second", Keywords = ["a,b"] }), "second-object");
        await sessions.CommitAsync(await PrepareCommitSessionAsync(sessions,
            CreateSessionRequest() with { IdempotencyKey = "third", Keywords = ["a", "b"] }), "third-object");
        var query = new AttachmentPageQuery
        {
            LayerId = TestLayerId, FeatureId = TestFeatureId, Limit = 1,
            ContentTypes = [first.ContentType], Keywords = ["a,b"]
        };
        var page = Assert.Single(await _attachmentStore.QueryPageAsync(query));
        Assert.Equal(first.Id, page.Attachment.Id);
        Assert.Equal(["a,b"], page.Metadata!.Keywords);
        var next = Assert.Single(await _attachmentStore.QueryPageAsync(query with { AfterId = first.Id }));
        Assert.Equal(second.Id, next.Attachment.Id);
        Assert.Empty(await _attachmentStore.QueryPageAsync(query with { AfterId = second.Id }));
        Assert.Empty(await _attachmentStore.QueryPageAsync(query with { FeatureId = TestFeatureId + 1 }));
        Assert.Empty(await _attachmentStore.QueryPageAsync(query with { ContentTypes = ["nonexistent/type"] }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _attachmentStore.QueryPageAsync(query with { Limit = 502 }));
    }

    [Fact]
    public async Task NativeMetadata_CommitAndReplacement_PreserveExactKeywordsAndIdentity()
    {
        var sessions = await CreateUploadSessionStoreAsync();
        var request = CreateSessionRequest() with { Keywords = ["a,b", "", " spaced ", "λ"] };
        var key = await PrepareCommitSessionAsync(sessions, request);
        var attachment = await sessions.CommitAsync(key, "native-object");
        var query = new AttachmentNativeMetadataQuery(TestLayerId, TestFeatureId, [attachment.Id]);
        var metadata = Assert.Single(await _attachmentStore.GetNativeMetadataAsync(query)).Value;
        Assert.Equal(request.Keywords, metadata.Keywords);
        Assert.Equal(request.Sha256, metadata.Sha256);
        Assert.NotEqual(Guid.Empty, metadata.GlobalId);
        Assert.Empty(await _attachmentStore.GetNativeMetadataAsync(query with { FeatureId = TestFeatureId + 1 }));
        var replacement = await PrepareCommitSessionAsync(NewUploadSessionStore(), request with
        {
            IdempotencyKey = "replace-native", ReplaceAttachmentId = attachment.Id, Keywords = ["new,value"]
        });
        await sessions.CommitAsync(replacement, "replacement-object");
        var updated = Assert.Single(await _attachmentStore.GetNativeMetadataAsync(query)).Value;
        Assert.Equal(metadata.GlobalId, updated.GlobalId);
        Assert.Equal(["new,value"], updated.Keywords);
    }

    [Fact]
    public async Task NativeMetadata_LegacyReplacement_HidesStaleDescriptor()
    {
        var sessions = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(sessions, CreateSessionRequest());
        var attachment = await sessions.CommitAsync(key, "native-object");
        await _fixture.ExecuteAsync($"UPDATE \"{_schemaName}\".attachments SET storage_path = 'legacy-replacement' WHERE id = {attachment.Id};");
        Assert.Empty(await _attachmentStore.GetNativeMetadataAsync(new AttachmentNativeMetadataQuery(TestLayerId, TestFeatureId, [attachment.Id])));
    }

    [Fact]
    public async Task NativeMetadata_ReceiptFailure_RollsBackDescriptor()
    {
        var sessions = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(sessions, CreateSessionRequest());
        await _fixture.ExecuteAsync($"ALTER TABLE \"{_schemaName}\".attachment_upload_sessions ADD CONSTRAINT reject_commits CHECK (state <> 'committed');");
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => sessions.CommitAsync(key, "native-object"));
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM \"{_schemaName}\".attachment_native_metadata", connection);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

}
