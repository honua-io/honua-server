// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Import.FileImport.Resumable;
using Honua.TestKit.Attributes;

namespace Honua.Import.UnitTests.Import;

// Real raw directories and durable journals; these do not claim Redis or host
// readiness qualification. No provider listing supplies this census's evidence.
public sealed class LocalImportStorageCensusTests
{
    [UnitTest]
    public async Task CensusAccountsForAllManagedRootsAndUnpublishedProviderMetadata()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        disk.Census().VerifyEmpty();
        Directory.CreateDirectory(Path.Join(disk.Roots[ImportStorageRootKind.ProviderObjects], ".metadata"));
        disk.Census().VerifyEmpty();
        await disk.InitializeAsync();
        foreach (var kind in Enum.GetValues<ImportStorageRootKind>())
        {
            var reservation = Reservation(kind);
            Assert.True(await disk.Journal.TryRecordIntentAsync(reservation));
            await disk.WriteAsync(kind, reservation.PhysicalLocation!.RelativePath, 9);
            if (reservation.PhysicalLocation!.MetadataPath(reservation) is { } metadata)
            {
                await disk.WriteAsync(kind, metadata, 2);
                if (kind == ImportStorageRootKind.ProviderObjects)
                    await disk.WriteAsync(kind, metadata + "." + Guid.NewGuid().ToString("N") + ".tmp", 7);
            }
        }
        await disk.Census().VerifyAsync();
        var retained = 0;
        await foreach (var entry in disk.Journal.ReadRetainedAsync())
        {
            Assert.Equal(LocalImportStorageState.Reserved, entry.State);
            retained++;
        }
        Assert.Equal(3, retained);
    }

    [UnitTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RawContentWithoutProviderMetadataStillMustHaveAJournalReservation(int rootKind)
    {
        if (!OperatingSystem.IsLinux()) return;
        var kind = (ImportStorageRootKind)rootKind;
        using var disk = new Disk();
        await disk.InitializeAsync();
        var reservation = Reservation(kind);
        await disk.WriteAsync(kind, reservation.PhysicalLocation!.RelativePath, 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => disk.Census().VerifyAsync());
        Assert.True(await disk.Journal.TryRecordIntentAsync(reservation));
        await disk.Census().VerifyAsync();
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedPhysicalContentOrMetadataPreventsRecovery(bool metadata)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        await disk.InitializeAsync();
        var reservation = Reservation(ImportStorageRootKind.ProviderObjects);
        Assert.True(await disk.Journal.TryRecordIntentAsync(reservation));
        await disk.WriteAsync(ImportStorageRootKind.ProviderObjects,
            metadata ? reservation.PhysicalLocation!.MetadataPath(reservation)! : reservation.PhysicalLocation!.RelativePath,
            metadata ? 65537 : 17);
        await Assert.ThrowsAsync<InvalidDataException>(() => disk.Census().VerifyAsync());
    }

    [UnitTest]
    public async Task DuplicatePhysicalBindingAndUnboundLegacyReservationsCannotRecover()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        await disk.InitializeAsync();
        var first = Reservation(ImportStorageRootKind.ProcessingScratch);
        Assert.True(await disk.Journal.TryRecordIntentAsync(first));
        var second = first with { FileId = $"imports/resumable/{first.UploadId}/scratch-{Guid.NewGuid():N}" };
        Assert.True(await disk.Journal.TryRecordIntentAsync(second));
        await Assert.ThrowsAsync<InvalidDataException>(() => disk.Census().VerifyAsync());
        Assert.True(await disk.Journal.RetireDeletedAsync(second));
        await disk.Census().VerifyAsync();
        var unbound = Reservation(ImportStorageRootKind.ProviderObjects) with { PhysicalLocation = null };
        Assert.True(await disk.Journal.TryRecordIntentAsync(unbound));
        await Assert.ThrowsAsync<InvalidDataException>(() => disk.Census().VerifyAsync());
    }

    [UnitTest]
    public async Task MissingPhysicalContentRemainsAConservativeIntentAndItsBindingIsImmutable()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        await disk.InitializeAsync();
        var reservation = Reservation(ImportStorageRootKind.ProcessingScratch);
        Assert.True(await disk.Journal.TryRecordIntentAsync(reservation));
        await disk.Census().VerifyAsync();
        Assert.Equal(reservation, (await disk.Journal.ReadEntryAsync(reservation.FileId))!.Reservation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => disk.Journal.TryRecordIntentAsync(reservation with
        {
            PhysicalLocation = reservation.PhysicalLocation! with { RelativePath = reservation.PhysicalLocation!.RelativePath + "-different" }
        }));
    }

    [UnitTest]
    public async Task UnknownOrRepeatedTemporaryMetadataIsNotAnUnboundedRecoveryAllowance()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        await disk.InitializeAsync();
        var reservation = Reservation(ImportStorageRootKind.ProviderObjects);
        Assert.True(await disk.Journal.TryRecordIntentAsync(reservation));
        var metadata = reservation.PhysicalLocation!.MetadataPath(reservation)!;
        await disk.WriteAsync(ImportStorageRootKind.ProviderObjects, metadata + "." + Guid.NewGuid().ToString("N") + ".tmp", 1);
        await disk.Census().VerifyAsync();
        await disk.WriteAsync(ImportStorageRootKind.ProviderObjects, metadata + "." + Guid.NewGuid().ToString("N") + ".tmp", 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => disk.Census().VerifyAsync());
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinksAndFifosCannotMasqueradeAsAccountedRegularFiles(bool fifo)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        await disk.InitializeAsync();
        var reservation = Reservation(ImportStorageRootKind.SourceScratch);
        Assert.True(await disk.Journal.TryRecordIntentAsync(reservation));
        var path = Path.Join(disk.Roots[ImportStorageRootKind.SourceScratch], reservation.PhysicalLocation!.RelativePath);
        if (fifo)
        {
            using var process = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { path }, UseShellExecute = false })!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            await Assert.ThrowsAsync<IOException>(() => disk.Census().VerifyAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else
        {
            var outside = Path.Join(disk.Base, "outside");
            await File.WriteAllBytesAsync(outside, [1]);
            File.CreateSymbolicLink(path, outside);
            await Assert.ThrowsAsync<InvalidDataException>(() => disk.Census().VerifyAsync());
        }
    }

    [UnitTest]
    public async Task BootstrapRequiresExistingEmptyDisjointRoots()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        disk.Census().VerifyEmpty();
        await disk.WriteAsync(ImportStorageRootKind.SourceScratch, "unknown", 1);
        Assert.Throws<InvalidDataException>(() => disk.Census().VerifyEmpty());
        Assert.Throws<ArgumentException>(() => new LocalImportStorageCensus(disk.Journal,
            new Dictionary<ImportStorageRootKind, string> { [ImportStorageRootKind.ProviderObjects] = disk.Base }, 8));
        Directory.Delete(disk.Roots[ImportStorageRootKind.ProviderObjects]);
        Assert.ThrowsAny<IOException>(() => disk.Census());
    }

    private static ImportStorageReservation Reservation(ImportStorageRootKind kind)
    {
        var upload = Guid.NewGuid().ToString("N");
        var id = Guid.NewGuid().ToString("N");
        var fileId = $"imports/resumable/{upload}/" + (kind == ImportStorageRootKind.ProviderObjects ? id + ".part" : "scratch-" + id);
        var path = kind switch
        {
            ImportStorageRootKind.ProviderObjects => fileId,
            ImportStorageRootKind.SourceScratch => $"honua-upload-{upload}-{id}.tmp",
            _ => $"honua-processing-{upload}-{Guid.NewGuid():N}/shape/roads.dbf"
        };
        return new(upload, new("actor", "tenant", "target", "workspace", "task", 1), fileId,
            kind == ImportStorageRootKind.ProcessingScratch ? 16 + ImportProcessingScratch.FileAccountingOverhead : 16)
        { PhysicalLocation = new(kind, path, 16) };
    }

    private sealed class Disk : IDisposable
    {
        internal string Base { get; } = Path.Join(Path.GetTempPath(), "honua-census-" + Guid.NewGuid().ToString("N"));
        internal Dictionary<ImportStorageRootKind, string> Roots { get; } = [];
        internal LocalImportStorageJournal Journal { get; }
        internal Disk()
        {
            Directory.CreateDirectory(Base);
            foreach (var kind in Enum.GetValues<ImportStorageRootKind>())
            {
                var root = Path.Join(Base, kind.ToString());
                Directory.CreateDirectory(root);
                Roots.Add(kind, root);
            }
            Journal = new(Path.Join(Base, "journal"), "domain", 8);
        }
        internal LocalImportStorageCensus Census() => new(Journal, Roots, 8);
        internal Task InitializeAsync() => Journal.CreateEmptyAsync(Guid.NewGuid().ToString("N"));
        internal async Task WriteAsync(ImportStorageRootKind kind, string relative, int length)
        {
            var path = Path.Join(Roots[kind], relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, new byte[length]);
        }
        public void Dispose() => Directory.Delete(Base, recursive: true);
    }
}
