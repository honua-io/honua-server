// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Import.FileImport.Resumable;
using Honua.TestKit.Attributes;

namespace Honua.Import.UnitTests.Import;

// Actual scratch files and controlled admission; cross-process flock and Redis
// recovery are covered separately and are not inferred from these lease cases.
public sealed class ImportScratchStorageFenceTests
{
    [UnitTest]
    public async Task ScratchRetainsFenceUntilDeletionAndReacquiresItForFailedReleaseRetry()
    {
        using var disk = new Disk();
        var fence = new Fence();
        var budget = new Budget();
        budget.Reserve = () =>
        {
            Assert.Equal(1, fence.Active);
            return Task.FromResult(true);
        };
        var releases = 0;
        budget.Release = () =>
        {
            Assert.Equal(1, fence.Active);
            Assert.Empty(Directory.EnumerateFiles(disk.Root));
            return ++releases == 1 ? Task.FromException<bool>(new IOException("Lost accounting reply")) : Task.FromResult(true);
        };
        var storage = new ImportScratchStorage(disk.Root, budget, fence);
        Assert.True(storage.IsBoundToMutationFence(fence));
        var lease = await storage.AllocateAsync(Session(), disk.Root, 64);
        Assert.Equal(1, fence.Active);
        await lease.Content.WriteAsync(new byte[64]);
        await Assert.ThrowsAsync<IOException>(async () => await lease.DisposeAsync());
        Assert.False(lease.Content.CanWrite);
        Assert.Equal(0, fence.Active);
        await lease.DisposeAsync();
        Assert.Equal(2, fence.Entered);
        Assert.Equal(2, releases);
        Assert.Equal(0, fence.Active);
        await lease.DisposeAsync();
        Assert.Equal(2, releases);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrUncertainReservationClosesFenceWithoutCreatingScratch(bool lostReply)
    {
        using var disk = new Disk();
        var fence = new Fence();
        var budget = new Budget();
        budget.Reserve = () =>
        {
            Assert.Equal(1, fence.Active);
            return lostReply ? Task.FromException<bool>(new IOException("Lost reservation reply")) : Task.FromResult(false);
        };
        var storage = new ImportScratchStorage(disk.Root, budget, fence);
        if (lostReply)
            await Assert.ThrowsAsync<IOException>(() => storage.AllocateAsync(Session(), disk.Root, 64));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.AllocateAsync(Session(), disk.Root, 64));
        Assert.Equal(0, fence.Active);
        Assert.False(Directory.Exists(disk.Root));
        Assert.Equal(0, budget.ReleaseCalls);
    }

    [UnitTest]
    public async Task DeletedScratchRemainsChargedWhenParentCannotBeDurablySynchronized()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var disk = new Disk();
        var fence = new Fence();
        var budget = new Budget();
        var lease = await new ImportScratchStorage(disk.Root, budget, fence).AllocateAsync(Session(), disk.Root, 64);
        var moved = disk.Root + "-moved";
        // Keep the same actual scratch directory reachable for deletion, but
        // make its configured parent unfit for the no-follow directory fsync.
        Directory.Move(disk.Root, moved);
        Directory.CreateSymbolicLink(disk.Root, moved);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => lease.DisposeAsync().AsTask());
            Assert.Empty(Directory.EnumerateFiles(moved));
            Assert.Equal(0, budget.ReleaseCalls);
            Assert.Equal(0, fence.Active);
        }
        finally
        {
            Directory.Delete(disk.Root);
            Directory.Move(moved, disk.Root);
            await lease.DisposeAsync();
        }
        Assert.Equal(1, budget.ReleaseCalls);
        Assert.Equal(2, fence.Entered);
    }

    private static ImportUploadSession Session() => new()
    {
        Id = Guid.NewGuid().ToString("N"), Scope = new("owner", "tenant", "target", "workspace", "task", 1),
        IntentSha256 = new string('a', 64), FileName = "source.gpkg", TotalBytes = 64,
        ChunkBytes = 64 * 1024, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };

    private sealed class Budget : IImportStorageBudget
    {
        internal Func<Task<bool>> Reserve = () => Task.FromResult(true);
        internal Func<Task<bool>> Release = () => Task.FromResult(true);
        internal int ReleaseCalls;
        public Task<bool> TryReserveAsync(ImportStorageReservation reservation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Reserve();
        }
        public Task<bool> ReleaseDeletedAsync(ImportStorageReservation reservation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReleaseCalls++;
            return Release();
        }
    }

    private sealed class Fence : IStorageDomainMutationFence
    {
        internal int Active;
        internal int Entered;
        public ValueTask<IAsyncDisposable> EnterMutationAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Active++;
            Entered++;
            return ValueTask.FromResult<IAsyncDisposable>(new Lease(this));
        }
        private sealed class Lease(Fence fence) : IAsyncDisposable
        {
            private bool _closed;
            public ValueTask DisposeAsync()
            {
                if (!_closed) { _closed = true; fence.Active--; }
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class Disk : IDisposable
    {
        internal string Root { get; } = Path.Join(Path.GetTempPath(), "honua-scratch-fence-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
