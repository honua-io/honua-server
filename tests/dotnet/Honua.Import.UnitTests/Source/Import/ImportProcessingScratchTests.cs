// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Import.FileImport.Resumable;
using Honua.TestKit.Attributes;

namespace Honua.Import.UnitTests.Import;

public sealed class ImportProcessingScratchTests
{
    [UnitTest]
    public async Task WorkspaceHoldsMutationFenceThroughPhysicalWritesAndAccountingCleanup()
    {
        using var fixture = new Fixture();
        fixture.Fence.BeforeAcquire = () => Assert.False(Directory.Exists(fixture.Root));
        await using var scratch = await fixture.CreateAsync();
        fixture.Budget.Directory = scratch.DirectoryPath;
        Assert.Equal(1, fixture.Fence.Active);
        using var source = new ReadObserverStream([1, 2, 3], () => Assert.Equal(1, fixture.Fence.Active));
        await scratch.WriteFileAsync(source, Path.Join(scratch.DirectoryPath, "data"), 3);
        Assert.Equal(1, fixture.Fence.Active);
        await scratch.DisposeAsync();
        Assert.Equal(0, fixture.Fence.Active);
        Assert.Equal(1, fixture.Fence.Acquired);
        Assert.Single(fixture.Budget.Released);
    }

    [UnitTest]
    public async Task CleanupFailureReleasesMutationFenceAndRetryReacquiresBeforeReleasingAccounting()
    {
        using var fixture = new Fixture();
        var scratch = await fixture.CreateAsync();
        fixture.Budget.Directory = scratch.DirectoryPath;
        using var source = new MemoryStream([1]);
        await scratch.WriteFileAsync(source, Path.Join(scratch.DirectoryPath, "data"), 1);
        fixture.Budget.ReleaseUnavailable = true;
        await Assert.ThrowsAsync<IOException>(() => scratch.DisposeAsync().AsTask());
        Assert.Equal(0, fixture.Fence.Active);
        Assert.Empty(fixture.Budget.Released);
        fixture.Budget.ReleaseUnavailable = false;
        await scratch.DisposeAsync();
        Assert.Equal(2, fixture.Fence.Acquired);
        Assert.Equal(0, fixture.Fence.Active);
        Assert.Single(fixture.Budget.Released);
    }

    [UnitTest]
    public async Task PhysicalWorkspaceCreationFailureReleasesMutationFence()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.Root, "not a directory");
        try
        {
            await Assert.ThrowsAsync<IOException>(() => fixture.CreateAsync());
            Assert.Equal(1, fixture.Fence.Acquired);
            Assert.Equal(0, fixture.Fence.Active);
        }
        finally { File.Delete(fixture.Root); }
    }

    [UnitTest]
    public async Task ExactCopy_ReservesBeforeReadingAndReleasesOnlyAfterDeletion()
    {
        using var fixture = new Fixture();
        await using var scratch = await fixture.CreateAsync();
        fixture.Budget.Directory = scratch.DirectoryPath;
        var path = Path.Join(scratch.DirectoryPath, "shape", "roads.dbf");
        using var source = new ReadObserverStream([1, 2, 3], () => Assert.Single(fixture.Budget.Reservations));
        await scratch.WriteFileAsync(source, path, 3);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        var reservation = Assert.Single(fixture.Budget.Reservations);
        Assert.Equal(3 + ImportProcessingScratch.FileAccountingOverhead, reservation.Bytes);
        Assert.Equal("owner", reservation.Scope.ActorId);
        var manifestPath = Assert.Single(Directory.GetFiles(Path.Join(scratch.DirectoryPath, ".reservations")));
        var manifest = JsonSerializer.Deserialize(await File.ReadAllTextAsync(manifestPath),
            ImportProcessingScratchJsonContext.Default.ImportProcessingScratchReservation);
        Assert.Equal(reservation, manifest!.Reservation);
        Assert.Equal(Path.Join("shape", "roads.dbf"), manifest.RelativePath);
        Assert.Empty(fixture.Budget.Released);
        await scratch.DisposeAsync();
        Assert.Equal(reservation, Assert.Single(fixture.Budget.Released));
        Assert.False(Directory.Exists(scratch.DirectoryPath));
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeniedOrUnknownAdmission_DoesNotReadOrWriteContent(bool unknown)
    {
        using var fixture = new Fixture();
        fixture.Budget.Allow = false;
        fixture.Budget.Unknown = unknown;
        await using var scratch = await fixture.CreateAsync();
        fixture.Budget.Directory = scratch.DirectoryPath;
        var path = Path.Join(scratch.DirectoryPath, "data.bin");
        using var source = new ReadObserverStream([1], () => Assert.Fail("Source read before admission."));
        await Assert.ThrowsAnyAsync<Exception>(() => scratch.WriteFileAsync(source, path, 1));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(scratch.DirectoryPath, "*", SearchOption.AllDirectories));
        Assert.Empty(fixture.Budget.Released);
    }

    [UnitTheory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task IncorrectDeclaredLength_RemainsChargedUntilCleanup(int expected)
    {
        using var fixture = new Fixture();
        await using var scratch = await fixture.CreateAsync();
        fixture.Budget.Directory = scratch.DirectoryPath;
        var path = Path.Join(scratch.DirectoryPath, "data.bin");
        using var source = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => scratch.WriteFileAsync(source, path, expected));
        Assert.True(new FileInfo(path).Length <= expected);
        Assert.Single(fixture.Budget.Reservations);
        Assert.Empty(fixture.Budget.Released);
    }

    [UnitTest]
    public async Task AggregateAllowance_IncludesRetainedEarlierFiles()
    {
        using var fixture = new Fixture();
        await using var scratch = await fixture.CreateAsync(ImportProcessingScratch.FileAccountingOverhead + 3);
        fixture.Budget.Directory = scratch.DirectoryPath;
        using var first = new MemoryStream([1, 2, 3]);
        await scratch.WriteFileAsync(first, Path.Join(scratch.DirectoryPath, "first"), 3);
        using var second = new ReadObserverStream([4], () => Assert.Fail("Source read after aggregate allowance exhausted."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scratch.WriteFileAsync(second, Path.Join(scratch.DirectoryPath, "second"), 1));
        Assert.Single(fixture.Budget.Reservations);
    }

    [UnitTest]
    public async Task OutsideWorkspace_RefusesBeforeReservationOrSourceRead()
    {
        using var fixture = new Fixture();
        await using var scratch = await fixture.CreateAsync();
        fixture.Budget.Directory = scratch.DirectoryPath;
        using var source = new ReadObserverStream([1], () => Assert.Fail("Source read for escaped path."));
        await Assert.ThrowsAsync<ArgumentException>(() => scratch.WriteFileAsync(source, Path.Join(fixture.Root, "outside"), 1));
        Assert.Empty(fixture.Budget.Reservations);
    }

    [UnitTest]
    public async Task ContentDeletionFails_PreservesManifestAndChargeUntilCleanupRetry()
    {
        using var fixture = new Fixture();
        await using var scratch = await fixture.CreateAsync();
        fixture.Budget.Directory = scratch.DirectoryPath;
        var path = Path.Join(scratch.DirectoryPath, "data.bin");
        using var source = new MemoryStream([1]);
        await scratch.WriteFileAsync(source, path, 1);
        File.Delete(path);
        Directory.CreateDirectory(path); // Real filesystem refuses File.Delete on a directory.
        await Assert.ThrowsAnyAsync<Exception>(() => scratch.DisposeAsync().AsTask());
        Assert.Single(Directory.GetFiles(Path.Join(scratch.DirectoryPath, ".reservations")));
        Assert.Empty(fixture.Budget.Released);
        using var later = new MemoryStream([2]);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scratch.WriteFileAsync(later, Path.Join(scratch.DirectoryPath, "later"), 1));
        Directory.Delete(path);
        await scratch.DisposeAsync();
        Assert.Single(fixture.Budget.Released);
    }

    [UnitTest]
    public async Task AccountingReleaseFails_ClosedWorkspaceCanRetryWithoutAllowingNewWriters()
    {
        using var fixture = new Fixture();
        await using var scratch = await fixture.CreateAsync();
        fixture.Budget.Directory = scratch.DirectoryPath;
        using var source = new MemoryStream([1]);
        await scratch.WriteFileAsync(source, Path.Join(scratch.DirectoryPath, "data"), 1);
        fixture.Budget.ReleaseUnavailable = true;
        await Assert.ThrowsAsync<IOException>(() => scratch.DisposeAsync().AsTask());
        Assert.False(Directory.Exists(scratch.DirectoryPath));
        Assert.Empty(fixture.Budget.Released);
        using var later = new MemoryStream([2]);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scratch.WriteFileAsync(later, Path.Join(scratch.DirectoryPath, "later"), 1));
        fixture.Budget.ReleaseUnavailable = false;
        await scratch.DisposeAsync();
        Assert.Single(fixture.Budget.Released);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Join(Path.GetTempPath(), "honua-processing-test-" + Guid.NewGuid().ToString("N"));
        internal Fence Fence { get; } = new();
        internal Budget Budget { get; }
        internal Fixture() => Budget = new(Fence);
        internal Task<IImportProcessingScratch> CreateAsync(long maximumBytes = 1024 * 1024)
            => new ImportProcessingScratchFactory(Root, Budget, Fence).CreateAsync(
                new(new string('a', 32), "owner", "tenant", "target", "workspace", "task", 1, new string('b', 64)), maximumBytes, 10);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class Fence : IStorageDomainMutationFence
    {
        internal int Active;
        internal int Acquired;
        internal Action? BeforeAcquire;
        public ValueTask<IAsyncDisposable> EnterMutationAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeAcquire?.Invoke();
            Active++;
            Acquired++;
            return ValueTask.FromResult<IAsyncDisposable>(new Lease(this));
        }
        private sealed class Lease(Fence owner) : IAsyncDisposable
        {
            private bool _released;
            public ValueTask DisposeAsync()
            {
                if (!_released) { owner.Active--; _released = true; }
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class Budget(Fence fence) : IImportStorageBudget
    {
        internal string? Directory { get; set; }
        internal bool Allow { get; set; } = true;
        internal bool Unknown { get; set; }
        internal bool ReleaseUnavailable { get; set; }
        internal List<ImportStorageReservation> Reservations { get; } = [];
        internal List<ImportStorageReservation> Released { get; } = [];
        public Task<bool> TryReserveAsync(ImportStorageReservation reservation, CancellationToken cancellationToken = default)
        {
            Assert.Equal(1, fence.Active);
            Reservations.Add(reservation);
            if (Unknown) throw new IOException("Admission reply lost.");
            return Task.FromResult(Allow);
        }
        public Task<bool> ReleaseDeletedAsync(ImportStorageReservation reservation, CancellationToken cancellationToken = default)
        {
            Assert.Equal(1, fence.Active);
            Assert.False(System.IO.Directory.Exists(Directory), "Accounting must survive until physical deletion.");
            if (ReleaseUnavailable) throw new IOException("Accounting unavailable.");
            Released.Add(reservation);
            return Task.FromResult(true);
        }
    }

    private sealed class ReadObserverStream(byte[] bytes, Action beforeRead) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            beforeRead();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
