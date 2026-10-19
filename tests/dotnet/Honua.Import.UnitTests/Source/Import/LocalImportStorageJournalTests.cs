// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Import.FileImport.Resumable;
using Honua.TestKit.Attributes;

namespace Honua.Import.UnitTests.Import;

// Real local preallocation, directory fsync and independent flock handles. These
// tests qualify the journal primitive, not physical census or Redis reconstruction.
public sealed class LocalImportStorageJournalTests
{
    [UnitTest]
    public async Task UninitializedJournalDoesNotBecomeEmptyAndExistingJournalCannotBeRecreated()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        var journal = disk.Open();
        await Assert.ThrowsAsync<FileNotFoundException>(() => journal.ReadEpochAsync());
        var epoch = Guid.NewGuid().ToString("N");
        await journal.CreateEmptyAsync(epoch);
        Assert.Equal(epoch, await disk.Open().ReadEpochAsync());
        await Assert.ThrowsAsync<IOException>(() => journal.CreateEmptyAsync(Guid.NewGuid().ToString("N")));
        Assert.Equal(epoch, await journal.ReadEpochAsync());
        Assert.Equal(journal.AllocatedBytes, new FileInfo(Path.Join(disk.Root, "reservations.bin")).Length);
    }

    [UnitTest]
    public async Task RestartPreservesImmutableIntentAndExactPhysicalMutationState()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        var journal = disk.Open();
        await journal.CreateEmptyAsync(Guid.NewGuid().ToString("N"));
        var reservation = Reservation();
        Assert.True(await journal.TryRecordIntentAsync(reservation));
        Assert.True(await journal.TryRecordIntentAsync(reservation));
        Assert.True(await journal.MarkWriteStartedAsync(reservation));
        var restarted = disk.Open();
        var row = Assert.Single(await ReadAll(restarted));
        Assert.Equal(reservation, row.Reservation);
        Assert.Equal(LocalImportStorageState.Writing, row.State);
        Assert.False(await restarted.MarkWriteStartedAsync(reservation));
        Assert.False(await restarted.RetireDeletedAsync(reservation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.TryRecordIntentAsync(reservation with { Bytes = 17 }));
        Assert.True(await restarted.MarkDeletionStartedAsync(reservation));
        Assert.True(await restarted.RetireDeletedAsync(reservation));
        Assert.Empty(await ReadAll(disk.Open()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.TryRecordIntentAsync(reservation));
    }

    [UnitTest]
    public async Task FixedCapacityRejectsNewIntentUntilRetirementWithoutGrowingControlFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        var journal = disk.Open(2);
        await journal.CreateEmptyAsync(Guid.NewGuid().ToString("N"));
        var first = Reservation();
        var second = Reservation();
        var third = Reservation();
        Assert.True(await journal.TryRecordIntentAsync(first));
        Assert.True(await journal.TryRecordIntentAsync(second));
        Assert.False(await journal.TryRecordIntentAsync(third));
        // The controlled caller has not created content for these reserved intents.
        Assert.True(await journal.RetireDeletedAsync(first));
        Assert.True(await journal.TryRecordIntentAsync(third));
        var retained = await ReadAll(disk.Open(2));
        Assert.Equal(2, retained.Count);
        Assert.Contains(retained, row => row.Reservation == second);
        Assert.Contains(retained, row => row.Reservation == third);
        Assert.Equal(journal.AllocatedBytes, new FileInfo(Path.Join(disk.Root, "reservations.bin")).Length);
    }

    [UnitTest]
    public async Task IndependentInstancesSerializeConcurrentIntentsWithoutOverwritingSlots()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        var first = disk.Open(8);
        var second = disk.Open(8);
        await first.CreateEmptyAsync(Guid.NewGuid().ToString("N"));
        var reservations = Enumerable.Range(0, 8).Select(_ => Reservation()).ToArray();
        var results = await Task.WhenAll(reservations.Select((reservation, index) =>
            (index % 2 == 0 ? first : second).TryRecordIntentAsync(reservation)));
        Assert.All(results, result => Assert.True(result));
        var retained = await ReadAll(disk.Open(8));
        Assert.Equal(reservations.OrderBy(row => row.FileId), retained.Select(row => row.Reservation).OrderBy(row => row.FileId));
    }

    [UnitTest]
    public async Task RecoveryEpochCompareAndSwapPreservesEveryRetainedIntent()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        var journal = disk.Open();
        var original = Guid.NewGuid().ToString("N");
        var next = Guid.NewGuid().ToString("N");
        await journal.CreateEmptyAsync(original);
        var reservation = Reservation();
        Assert.True(await journal.TryRecordIntentAsync(reservation));
        Assert.True(await journal.AdvanceEpochAsync(original, next));
        Assert.False(await journal.AdvanceEpochAsync(original, Guid.NewGuid().ToString("N")));
        Assert.Equal(next, await disk.Open().ReadEpochAsync());
        Assert.Equal(reservation, Assert.Single(await ReadAll(journal)).Reservation);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalImportStorageJournal(disk.Root, "different-domain", 4).ReadEpochAsync());
    }

    [UnitTest]
    public async Task CorruptPageIsNotTreatedAsAnEmptyOrOlderJournal()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        var journal = disk.Open();
        var original = Guid.NewGuid().ToString("N");
        await journal.CreateEmptyAsync(original);
        Assert.True(await journal.AdvanceEpochAsync(original, Guid.NewGuid().ToString("N")));
        await using (var file = new FileStream(Path.Join(disk.Root, "reservations.bin"), FileMode.Open, FileAccess.Write))
        {
            // Damage the older page even though the other complete page still exists.
            await file.WriteAsync(new byte[] { 0 });
            file.Flush(flushToDisk: true);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => disk.Open().ReadEpochAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => journal.TryRecordIntentAsync(Reservation()));
    }

    [UnitTest]
    public async Task CancellationBeforeAdmissionDoesNotRecordAnIntent()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        var journal = disk.Open();
        await journal.CreateEmptyAsync(Guid.NewGuid().ToString("N"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.TryRecordIntentAsync(Reservation(), cancelled.Token));
        Assert.Empty(await ReadAll(journal));
    }

    private static ImportStorageReservation Reservation()
    {
        var upload = Guid.NewGuid().ToString("N");
        return new ImportStorageReservation(upload, new ImportUploadScope("actor", "tenant", "target", "workspace", "task", 1),
            $"imports/resumable/{upload}/{Guid.NewGuid():N}.part", 16);
    }

    [UnitTest]
    public async Task CanonicalMaximumUnicodeScopeAndPhysicalBindingFitWithoutNarrowingIdentityLimits()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        var journal = disk.Open();
        await journal.CreateEmptyAsync(Guid.NewGuid().ToString("N"));
        var original = Reservation();
        var reservation = original with
        {
            FileId = $"imports/resumable/{original.UploadId}/scratch-{Guid.NewGuid():N}",
            Bytes = 16 + ImportProcessingScratch.FileAccountingOverhead,
            Scope = new ImportUploadScope(new string('界', 512), new string('県', 256), new string('地', 256),
                new string('図', 256), new string('層', 256), 1),
            PhysicalLocation = new(ImportStorageRootKind.ProcessingScratch,
                $"honua-processing-{original.UploadId}-{Guid.NewGuid():N}/" + string.Join('/', Enumerable.Repeat(new string('路', 230), 5)), 16)
        };
        Assert.True(await journal.TryRecordIntentAsync(reservation));
        Assert.Equal(reservation, Assert.Single(await ReadAll(disk.Open())).Reservation);
    }

    private static async Task<List<LocalImportStorageJournalEntry>> ReadAll(LocalImportStorageJournal journal)
    {
        var result = new List<LocalImportStorageJournalEntry>();
        await foreach (var entry in journal.ReadRetainedAsync()) result.Add(entry);
        return result;
    }

    private sealed class Disk : IDisposable
    {
        internal string Root { get; } = Path.Join(Path.GetTempPath(), "honua-import-journal-" + Guid.NewGuid().ToString("N"));
        internal LocalImportStorageJournal Open(int capacity = 4) => new(Root, "test-storage-domain", capacity);
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
