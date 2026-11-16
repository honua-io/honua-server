// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests;

public partial class PostgresAttachmentStoreTests
{
    [IntegrationTheory]
    [Operation(Operations.QueryAttachments)]
    [InlineData("photo")]
    [InlineData("original")]
    public async Task NativeMetadata_LegacyKeywordFilter_MatchesIndividualTags(string keyword)
    {
        var attachment = await CreateTestAttachment();
        await _attachmentStore.UpdateKeywordsAsync(TestLayerId, TestFeatureId, attachment.Id, "photo,original");
        var page = await _attachmentStore.QueryPageAsync(new AttachmentPageQuery
        {
            LayerId = TestLayerId,
            FeatureId = TestFeatureId,
            Limit = 10,
            Keywords = [keyword]
        });
        var item = Assert.Single(page);
        Assert.Equal(attachment.Id, item.Attachment.Id);
        Assert.Null(item.Metadata);
    }

    [IntegrationTheory]

    [Operation(Operations.QueryAttachments)]
    [InlineData("photo,original")]
    [InlineData("missing")]
    public async Task NativeMetadata_LegacyKeywordFilter_RejectsNonMatchingTags(string keyword)
    {
        var attachment = await CreateTestAttachment();
        await _attachmentStore.UpdateKeywordsAsync(TestLayerId, TestFeatureId, attachment.Id, "photo,original");
        var page = await _attachmentStore.QueryPageAsync(new AttachmentPageQuery
        {
            LayerId = TestLayerId,
            FeatureId = TestFeatureId,
            Limit = 10,
            Keywords = [keyword]
        });
        Assert.Empty(page);
    }

    [IntegrationTheory]

    [Operation(Operations.UpdateAttachment)]
    [InlineData("new,keywords")]
    [InlineData(null)]
    public async Task NativeMetadata_LegacyKeywordEdit_UpdatesNativeReadsAndFilters(string? keywords)
    {
        var attachment = await CreateAttachmentWithNativeKeywordsAsync();
        await _attachmentStore.UpdateKeywordsAsync(TestLayerId, TestFeatureId, attachment.Id, keywords);
        await AssertNativeKeywordsAsync(attachment.Id, keywords);
    }

    [IntegrationTheory]

    [Operation(Operations.UpdateAttachment)]
    [InlineData("new,keywords")]
    [InlineData(null)]
    public async Task NativeMetadata_LegacyFullUpdate_UpdatesNativeReadsAndFilters(string? keywords)
    {
        var attachment = await CreateAttachmentWithNativeKeywordsAsync();
        await _attachmentStore.UpdateAsync(TestLayerId, TestFeatureId, attachment with { Keywords = keywords });
        await AssertNativeKeywordsAsync(attachment.Id, keywords);
    }

    [IntegrationTest]

    [Operation(Operations.UpdateAttachment)]
    public async Task NativeMetadata_FilenameEdit_PreservesExactKeywordBoundaries()
    {
        var sessions = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(sessions, CreateSessionRequest() with { Keywords = ["a,b", "λ"] });
        var attachment = await sessions.CommitAsync(key, AttachmentUploadObjectKey.ForSession(key.SessionId));
        var updated = await _attachmentStore.UpdateAsync(TestLayerId, TestFeatureId, attachment with { Filename = "renamed.txt" });
        Assert.Equal("renamed.txt", updated.Filename);
        var metadata = Assert.Single(await _attachmentStore.GetNativeMetadataAsync(
            new AttachmentNativeMetadataQuery(TestLayerId, TestFeatureId, [attachment.Id]))).Value;
        Assert.Equal(["a,b", "λ"], metadata.Keywords);
        var query = new AttachmentPageQuery { LayerId = TestLayerId, FeatureId = TestFeatureId, Limit = 10, Keywords = ["a,b"] };
        Assert.Equal(attachment.Id, Assert.Single(await _attachmentStore.QueryPageAsync(query)).Attachment.Id);
        Assert.Empty(await _attachmentStore.QueryPageAsync(query with { Keywords = ["a"] }));
    }

    private async Task<Attachment> CreateAttachmentWithNativeKeywordsAsync()
    {
        var sessions = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(sessions, CreateSessionRequest() with { Keywords = ["old"] });
        return await sessions.CommitAsync(key, AttachmentUploadObjectKey.ForSession(key.SessionId));
    }

    private async Task AssertNativeKeywordsAsync(long attachmentId, string? keywords)
    {
        var metadata = Assert.Single(await _attachmentStore.GetNativeMetadataAsync(
            new AttachmentNativeMetadataQuery(TestLayerId, TestFeatureId, [attachmentId]))).Value;
        Assert.Equal(keywords?.Split(',') ?? [], metadata.Keywords);
        var query = new AttachmentPageQuery { LayerId = TestLayerId, FeatureId = TestFeatureId, Limit = 10, Keywords = ["old"] };
        Assert.Empty(await _attachmentStore.QueryPageAsync(query));
        if (keywords != null)
        {
            Assert.Equal(attachmentId, Assert.Single(await _attachmentStore.QueryPageAsync(query with { Keywords = ["new"] })).Attachment.Id);
        }
    }

    [IntegrationTest]

    [Operation(Operations.QueryAttachments)]
    public async Task NativeMetadata_QueryPage_BoundsAndScopesExactKeywordMatches()
    {
        var sessions = await CreateUploadSessionStoreAsync();
        var first = await CommitPreparedSessionAsync(sessions, await PrepareCommitSessionAsync(sessions,
            CreateSessionRequest() with { IdempotencyKey = "first", Keywords = ["a,b"] }));
        var second = await CommitPreparedSessionAsync(sessions, await PrepareCommitSessionAsync(sessions,
            CreateSessionRequest() with { IdempotencyKey = "second", Keywords = ["a,b"] }));
        await CommitPreparedSessionAsync(sessions, await PrepareCommitSessionAsync(sessions,
            CreateSessionRequest() with { IdempotencyKey = "third", Keywords = ["a", "b"] }));
        var query = new AttachmentPageQuery
        {
            LayerId = TestLayerId,
            FeatureId = TestFeatureId,
            Limit = 1,
            ContentTypes = [first.ContentType],
            Keywords = ["a,b"]
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

    [IntegrationTest]

    [Operation(Operations.UpdateAttachment)]
    public async Task NativeMetadata_CommitAndReplacement_PreserveExactKeywordsAndIdentity()
    {
        var sessions = await CreateUploadSessionStoreAsync();
        var request = CreateSessionRequest() with { Keywords = ["a,b", "", " spaced ", "λ"] };
        var key = await PrepareCommitSessionAsync(sessions, request);
        var attachment = await CommitPreparedSessionAsync(sessions, key);
        var query = new AttachmentNativeMetadataQuery(TestLayerId, TestFeatureId, [attachment.Id]);
        var metadata = Assert.Single(await _attachmentStore.GetNativeMetadataAsync(query)).Value;
        Assert.Equal(request.Keywords, metadata.Keywords);
        Assert.Equal(request.Sha256, metadata.Sha256);
        Assert.NotEqual(Guid.Empty, metadata.GlobalId);
        Assert.Empty(await _attachmentStore.GetNativeMetadataAsync(query with { FeatureId = TestFeatureId + 1 }));
        var replacement = await PrepareCommitSessionAsync(NewUploadSessionStore(), request with
        {
            IdempotencyKey = "replace-native",
            ReplaceAttachmentId = attachment.Id,
            Keywords = ["new,value"]
        });
        await CommitPreparedSessionAsync(sessions, replacement);
        var updated = Assert.Single(await _attachmentStore.GetNativeMetadataAsync(query)).Value;
        Assert.Equal(metadata.GlobalId, updated.GlobalId);
        Assert.Equal(["new,value"], updated.Keywords);
        var replay = await NewUploadSessionStore().CommitAsync(key, "ignored-object");
        Assert.Equivalent(attachment, replay);
        Assert.Equal(request.Keywords, replay.NativeMetadata!.Keywords);
        Assert.Equal(request.Sha256, replay.NativeMetadata.Sha256);
        await _fixture.ExecuteAsync($"DELETE FROM \"{_schemaName}\".attachments WHERE id = {attachment.Id};");
        Assert.Equivalent(attachment, await NewUploadSessionStore().CommitAsync(key, "ignored-after-delete"));
    }

    [IntegrationTest]

    [Operation(Operations.UpdateAttachment)]
    public async Task NativeMetadata_LegacyReplacement_HidesStaleDescriptor()
    {
        var sessions = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(sessions, CreateSessionRequest());
        var attachment = await CommitPreparedSessionAsync(sessions, key);
        await _fixture.ExecuteAsync($"UPDATE \"{_schemaName}\".attachments SET storage_path = 'legacy-replacement' WHERE id = {attachment.Id};");
        Assert.Empty(await _attachmentStore.GetNativeMetadataAsync(new AttachmentNativeMetadataQuery(TestLayerId, TestFeatureId, [attachment.Id])));
    }

    [IntegrationTest]

    [Operation(Operations.UpdateAttachment)]
    public async Task NativeMetadata_ReceiptFailure_RollsBackDescriptor()
    {
        var sessions = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(sessions, CreateSessionRequest());
        await _fixture.ExecuteAsync($"ALTER TABLE \"{_schemaName}\".attachment_upload_sessions ADD CONSTRAINT reject_commits CHECK (state <> 'committed');");
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => CommitPreparedSessionAsync(sessions, key));
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM \"{_schemaName}\".attachment_native_metadata", connection);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

}
