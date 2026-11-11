// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresUniversalProgressStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task GetProgressAsync_OnASecondStore_ReturnsTheSnapshotLastWritten()
    {
        await using var harness = await CreateHarnessAsync();
        var queued = Export(OperationStatus.Queued);
        await harness.CreateProgressStore().SetProgressAsync(queued.JobId, queued);
        await harness.CreateProgressStore().SetProgressAsync(queued.JobId, queued with { Status = OperationStatus.Processing, ProcessedFeatures = 40 });

        var read = await harness.CreateProgressStore().GetProgressAsync<ExportProgress>(queued.JobId);

        read!.Status.Should().Be(OperationStatus.Processing);
        read.ProcessedFeatures.Should().Be(40);
        (await harness.CreateProgressStore().GetProgressAsync("no-such-operation")).Should().BeNull();
    }

    [IntegrationTest]
    public async Task GetProgressAsync_CompletedImportReadByAReplacementTask_ReturnsTheImportRecord()
    {
        await using var harness = await CreateHarnessAsync();
        var import = new ImportProgress
        {
            JobId = $"import-{Guid.NewGuid():N}",
            Status = ImportStatus.Completed,
            TableName = "parcels",
            Format = SupportedFileFormat.Csv,
            FeaturesProcessed = 1200,
            CompletedAt = DateTimeOffset.UtcNow,
        };
        await harness.CreateProgressStore().SetProgressAsync(import.JobId, import, TimeSpan.FromDays(1));

        var read = await harness.CreateProgressStore().GetProgressAsync<ImportProgress>(import.JobId);

        read!.Status.Should().Be(ImportStatus.Completed);
        read.FeaturesProcessed.Should().Be(1200);
        read.TableName.Should().Be("parcels");
    }

    [IntegrationTest]
    public async Task TrySetProgressAsync_ExpectedStatusChanged_IsRefused()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateProgressStore();
        var processing = Export(OperationStatus.Processing);
        await store.SetProgressAsync(processing.JobId, processing);
        await store.SetProgressAsync(processing.JobId, processing with { Status = OperationStatus.Completed });

        var lateCancel = await store.TrySetProgressAsync(new OperationProgressWrite(processing.JobId, processing with { Status = OperationStatus.Cancelled }), OperationStatus.Processing);

        lateCancel.Outcome.Should().Be(ProgressCompareAndSetOutcome.StatusMismatch);
        lateCancel.CurrentProgress!.Status.Should().Be(OperationStatus.Completed);
        (await store.GetProgressAsync(processing.JobId))!.Status.Should().Be(OperationStatus.Completed);
        (await store.TrySetProgressAsync(new OperationProgressWrite("no-such-operation", processing), OperationStatus.Processing))
            .Outcome.Should().Be(ProgressCompareAndSetOutcome.NotFound);
    }

    [IntegrationTest]
    public async Task TryReplaceProgressAsync_TwoWriters_ExactlyOneWins()
    {
        await using var harness = await CreateHarnessAsync();
        var read = Export(OperationStatus.Processing);
        await harness.CreateProgressStore().SetProgressAsync(read.JobId, read);

        var outcomes = await Task.WhenAll(Enumerable.Range(1, 8).Select(writer =>
            harness.CreateProgressStore().TryReplaceProgressAsync(new OperationProgressWrite(read.JobId, read with { ProcessedFeatures = writer }), read)));

        outcomes.Count(outcome => outcome.Outcome == ProgressCompareAndSetOutcome.Updated).Should().Be(1);
        outcomes.Where(outcome => outcome.Outcome != ProgressCompareAndSetOutcome.Updated)
            .Should().OnlyContain(outcome => outcome.Outcome == ProgressCompareAndSetOutcome.StatusMismatch);
    }

    [IntegrationTest]
    public async Task GetActiveOperationIdsAsync_ListsLiveSnapshotsOfTheTypeUntilTheyExpireOrAreDeleted()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateProgressStore();
        var kept = Export(OperationStatus.Processing);
        var deleted = Export(OperationStatus.Processing);
        var expired = Export(OperationStatus.Processing);
        foreach (var snapshot in new[] { kept, deleted, expired })
        {
            await store.SetProgressAsync(snapshot.JobId, snapshot);
        }

        await store.DeleteProgressAsync(deleted.JobId);
        await harness.ExecuteAsync(
            $"UPDATE {harness.Table("operation_progress")} SET expires_at = now() - interval '1 second' WHERE operation_id = @id",
            new NpgsqlParameter("id", expired.JobId));

        (await store.GetActiveOperationIdsAsync(OperationType.Export)).Should().Equal(kept.JobId);
        (await store.GetActiveOperationIdsAsync()).Should().Equal(kept.JobId);
        (await store.GetActiveOperationIdsAsync(OperationType.Upload)).Should().BeEmpty();
        (await store.GetActiveOperationsAsync<ExportProgress>(OperationType.Export)).Should().ContainSingle();
        (await store.GetProgressAsync(expired.JobId)).Should().BeNull();
        (await store.DeleteExpiredAsync(batchSize: 100, CancellationToken.None)).Should().Be(1);
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresUniversalProgressStoreTests));

    private static ExportProgress Export(OperationStatus status)
        => new()
        {
            JobId = $"export-{Guid.NewGuid():N}",
            Format = "geojson",
            ServiceName = "parcels",
            LayerId = 0,
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
        };
}
