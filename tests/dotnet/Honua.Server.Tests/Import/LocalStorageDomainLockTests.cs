// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Import.FileImport.Resumable;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Import;

public sealed class LocalStorageDomainLockTests
{
    [UnitTest]
    public async Task NestedSharedWriters_BlockExclusiveCensusUntilEveryLeaseCloses()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Join(Path.GetTempPath(), "honua-domain-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new LocalStorageDomainLock(path);
            var second = new LocalStorageDomainLock(path);
            var outer = await first.EnterAsync(false, default);
            var inner = await second.EnterAsync(false, default);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var census = second.EnterAsync(true, deadline.Token).AsTask();
            await outer.DisposeAsync();
            Assert.False(census.IsCompleted);
            await inner.DisposeAsync();
            await using var exclusive = await census;
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.EnterAsync(false, cancelled.Token).AsTask());
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
