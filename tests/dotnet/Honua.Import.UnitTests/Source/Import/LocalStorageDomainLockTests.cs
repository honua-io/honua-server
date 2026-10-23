// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Import.FileImport.Resumable;
using Honua.TestKit.Attributes;

namespace Honua.Import.UnitTests.Import;

public sealed class LocalStorageDomainLockTests
{
    [UnitTest]
    public async Task ExistingExclusiveLeaseMakesAnotherInstanceWaitUntilRelease()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Join(Path.GetTempPath(), "honua-lock-contention-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new LocalStorageDomainLock(directory);
            var second = new LocalStorageDomainLock(directory);
            await using var exclusive = await first.EnterAsync(true, default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var pending = second.EnterAsync(false, timeout.Token).AsTask();
            Assert.False(pending.IsCompleted);
            await exclusive.DisposeAsync();
            await using var shared = await pending;
            await using var nested = await first.EnterAsync(false, timeout.Token);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [UnitTest]
    public async Task ExternalExclusiveHolderAllowsCancellableWaitAndLaterAcquisition()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Join(Path.GetTempPath(), "honua-lock-process-" + Guid.NewGuid().ToString("N"));
        var control = new LocalStorageDomainLock(directory);
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo("flock")
            {
                ArgumentList = { "-x", Path.Join(directory, "domain.lock"), "sh", "-c", "printf 'ready\\n'; read ignored" },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            }
        };
        var started = false;
        try
        {
            started = child.Start();
            Assert.True(started);
            Assert.Equal("ready", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => control.EnterAsync(false, cancelled.Token).AsTask());
            await child.StandardInput.WriteLineAsync();
            await child.StandardInput.FlushAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, child.ExitCode);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var acquired = await control.EnterAsync(true, deadline.Token);
        }
        finally
        {
            if (started && !child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            Directory.Delete(directory, recursive: true);
        }
    }
}
