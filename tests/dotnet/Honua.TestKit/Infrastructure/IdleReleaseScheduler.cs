// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.TestKit.Infrastructure;

// Keeps an unreferenced process-wide test resource alive for an idle grace so the next case or
// class can reuse it, then releases it. The owner serializes all state with one gate; Schedule
// and Cancel are called with that gate held, and the release paths acquire it themselves.
// ProcessExit releases a resource still in its grace when the test host raises the event.
internal sealed class IdleReleaseScheduler(
    SemaphoreSlim gate,
    Func<TimeSpan> idleDelay,
    Func<Task> releaseIfIdle,
    string resourceName)
{
    private static readonly TimeSpan ProcessExitDisposalLimit = TimeSpan.FromSeconds(10);
    private CancellationTokenSource? _pending;
    private int _processExitHooked;

    // Callers hold the gate.
    public void Schedule()
    {
        Cancel();
        var pending = new CancellationTokenSource();
        _pending = pending;
        _ = ReleaseWhenIdleAsync(pending);
    }

    // Callers hold the gate.
    public void Cancel()
    {
        var pending = _pending;
        _pending = null;
        if (pending is null)
        {
            return;
        }

        pending.Cancel();
        pending.Dispose();
    }

    // Callers hold the gate.
    public async Task ReleaseIfIdleLockedAsync()
    {
        Cancel();
        await releaseIfIdle().ConfigureAwait(false);
    }

    public async Task ReleaseNowAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ReleaseIfIdleLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public void HookProcessExit()
    {
        if (Interlocked.Exchange(ref _processExitHooked, 1) == 0)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                ReleaseNowAsync().Wait(ProcessExitDisposalLimit);
        }
    }

    private async Task ReleaseWhenIdleAsync(CancellationTokenSource pending)
    {
        try
        {
            await Task.Delay(idleDelay(), pending.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_pending, pending))
            {
                await ReleaseIfIdleLockedAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // No test awaits this release; report a disposal failure instead of dropping it.
            await Console.Error.WriteLineAsync($"{resourceName} idle release failed: {exception}").ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
