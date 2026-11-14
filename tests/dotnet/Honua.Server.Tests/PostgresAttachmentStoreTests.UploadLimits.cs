// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;
using Honua.Db.Postgres.Features.Attachments;
using Honua.Server.Tests.Infrastructure;

namespace Honua.Server.Tests;

public partial class PostgresAttachmentStoreTests
{
    [Fact]
    public async Task CommitSession_FileExceedsCurrentLimit_KeepsSessionAndPublishesNothing()
    {
        var sessions = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(sessions, CreateSessionRequest());
        var publisher = new PostgresAttachmentStore(
            new TestDatabaseConnectionProvider(_fixture.DataSource), _fileStorage,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresAttachmentStore>.Instance, _schemaName,
            attachmentLimits: new Honua.Core.Configuration.AttachmentLimits
            {
                MaxAttachmentsPerFeature = 5, MaxAttachmentSize = 5, MaxTotalAttachmentSize = 1024
            }, migrationContract: Honua.Server.Startup.ServerCoreSchemaMigrations.Manifest.Attachments!);
        var restarted = new PostgresAttachmentUploadSessionStore(
            new TestDatabaseConnectionProvider(_fixture.DataSource), _schemaName, publisher);
        await Assert.ThrowsAsync<Honua.Core.Exceptions.ValidationException>(() => restarted.CommitAsync(key, "prepared-object"));
        Assert.Empty(await publisher.ListAsync(TestLayerId, TestFeatureId));
        Assert.Equal(AttachmentUploadState.Uploading, (await sessions.GetAsync(key)).State);
    }
}
