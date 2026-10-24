// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Attachments;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.Attachments;

[Trait("Tier", "Fast")]
public sealed class FeatureDeleteAttachmentCleanupTests
{
    [Fact]
    public void Hold_OpenBatch_StagesUntilCommitAndRollbackDiscards()
    {
        var pending = new PendingAttachmentBlobs();
        var first = new DeletedAttachmentBlob(4, 10, "blob-a");
        var second = new DeletedAttachmentBlob(4, 11, "blob-b");

        pending.Hold([first], batchTransactionOpen: true).Should().BeNull();
        pending.Hold([second], batchTransactionOpen: true).Should().BeNull();
        pending.Count.Should().Be(2);

        pending.Discard();
        pending.TakeForConfirmedCommit().Should().BeEmpty();
    }

    [Fact]
    public void Hold_OpenBatch_ConfirmedCommitReturnsEachPathOnce()
    {
        var pending = new PendingAttachmentBlobs();
        var blobs = new[]
        {
            new DeletedAttachmentBlob(4, 10, "blob-a"),
            new DeletedAttachmentBlob(4, 10, "blob-b"),
        };

        pending.Hold(blobs, batchTransactionOpen: true);
        pending.TakeForConfirmedCommit().Should().Equal(blobs);
        pending.TakeForConfirmedCommit().Should().BeEmpty();
    }

    [Fact]
    public void Hold_Autocommit_ReturnsBlobsImmediatelyAndDoesNotStage()
    {
        var pending = new PendingAttachmentBlobs();
        var blobs = new[] { new DeletedAttachmentBlob(4, 10, "blob-a") };

        pending.Hold(blobs, batchTransactionOpen: false).Should().Equal(blobs);
        pending.Count.Should().Be(0);
        pending.TakeForConfirmedCommit().Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteBlobsAsync_DeletesEveryBlob_AndContinuesWhenOneFails()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.DeleteAsync("ok", Arg.Any<CancellationToken>()).Returns(true);
        storage.DeleteAsync("missing", Arg.Any<CancellationToken>()).Returns(false);
        storage.DeleteAsync("failed", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new IOException("disk")));
        storage.DeleteAsync("canceled", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new OperationCanceledException()));
        var ledger = new RecordingLedger();
        var store = new PostgresAttachmentStore(
            Substitute.For<IAdoNetDatabaseConnectionProvider>(),
            storage,
            NullLogger<PostgresAttachmentStore>.Instance,
            "honua",
            ledger,
            migrationContract: new Honua.Db.Postgres.Features.Infrastructure.Migrations.PostgresAttachmentMigrationContract("test.attachments.create", "test.attachments.import", "test.preamble", "test.sha"));

        var act = () => store.DeleteBlobsAsync(
            [
                new DeletedAttachmentBlob(3, 9, "ok"),
                new DeletedAttachmentBlob(3, 9, "missing"),
                new DeletedAttachmentBlob(3, 9, "failed"),
                new DeletedAttachmentBlob(3, 9, "canceled"),
            ],
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        await storage.Received(1).DeleteAsync("ok", Arg.Any<CancellationToken>());
        await storage.Received(1).DeleteAsync("canceled", Arg.Any<CancellationToken>());
        ledger.Orphans.Select(orphan => orphan.StoragePath).Should().Equal("failed", "canceled");
        ledger.Orphans.Should().OnlyContain(orphan => orphan.Kind == AttachmentOrphanKind.UndeletedObject);
        ledger.Orphans.Should().OnlyContain(orphan => orphan.LayerId == 3 && orphan.FeatureId == 9);
    }

    [Fact]
    public async Task DeleteBlobsAsync_WithoutStorage_RecordsOrphansAndDoesNotThrow()
    {
        var ledger = new RecordingLedger();
        var store = new PostgresAttachmentStore(
            Substitute.For<IAdoNetDatabaseConnectionProvider>(),
            fileStorage: null,
            NullLogger<PostgresAttachmentStore>.Instance,
            "honua",
            ledger,
            migrationContract: new Honua.Db.Postgres.Features.Infrastructure.Migrations.PostgresAttachmentMigrationContract("test.attachments.create", "test.attachments.import", "test.preamble", "test.sha"));

        var act = () => store.DeleteBlobsAsync(
            [new DeletedAttachmentBlob(3, 9, "blob")],
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        ledger.Orphans.Should().ContainSingle();
        ledger.Orphans[0].StoragePath.Should().Be("blob");
        ledger.Orphans[0].Kind.Should().Be(AttachmentOrphanKind.UndeletedObject);
        ledger.Orphans[0].Reason.Should().Contain("not configured");
    }

    private sealed class RecordingLedger : IAttachmentOrphanLedger
    {
        public List<AttachmentOrphan> Orphans { get; } = new();

        public ValueTask RecordAsync(AttachmentOrphan orphan, CancellationToken cancellationToken = default)
        {
            Orphans.Add(orphan);
            return ValueTask.CompletedTask;
        }
    }
}
