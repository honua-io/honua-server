// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Migration;

internal static class ImportCoordinationRecovery
{
    private static readonly TimeSpan _componentProbeTimeout = TimeSpan.FromSeconds(5);

    // One in-flight probe per component instance. A timeout only returns the caller;
    // the gate stays held until that probe finishes so the next tick does not start another.
    private static readonly ConditionalWeakTable<object, SemaphoreSlim> _probeGates = new();

    public static async Task ProbeAsync<TRequest, TProgress>(
        IDistributedJobQueueService jobQueue,
        IDistributedProgressStore<TRequest> requestStore,
        IDistributedProgressStore<TProgress> progressStore,
        CancellationToken cancellationToken)
        where TRequest : class
        where TProgress : class
    {
        // The queue length read is the queue's throttled restore path; it never dequeues.
        await ProbeComponentAsync(jobQueue, jobQueue.GetQueueLengthAsync, cancellationToken).ConfigureAwait(false);
        if (requestStore is IProgressStoreRecovery requestRecovery)
        {
            await ProbeComponentAsync(requestStore, requestRecovery.ProbeRecoveryAsync, cancellationToken).ConfigureAwait(false);
        }

        if (progressStore is IProgressStoreRecovery progressRecovery)
        {
            await ProbeComponentAsync(progressStore, progressRecovery.ProbeRecoveryAsync, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<bool> TryAdmitAsync<TRequest, TProgress>(
        IImportCoordinationHealth health,
        IDistributedJobQueueService jobQueue,
        IDistributedProgressStore<TRequest> requestStore,
        IDistributedProgressStore<TProgress> progressStore,
        CancellationToken cancellationToken)
        where TRequest : class
        where TProgress : class
    {
        if (health.CanAcceptNewJobs)
        {
            return true;
        }

        await ProbeAsync(jobQueue, requestStore, progressStore, cancellationToken).ConfigureAwait(false);
        return health.CanAcceptNewJobs;
    }

    private static async Task ProbeComponentAsync(
        object component,
        Func<CancellationToken, Task> start,
        CancellationToken cancellationToken)
    {
        var gate = _probeGates.GetValue(component, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_componentProbeTimeout);
        Task? probe = null;
        try
        {
            probe = start(timeout.Token);
            await probe.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Still unavailable: the component keeps its cached failure flag. Move on so
            // one hung dependency cannot stall recovery of the others. The gate stays
            // held until this probe finishes.
        }
        finally
        {
            if (probe is null)
            {
                timeout.Dispose();
                gate.Release();
            }
            else
            {
                ObserveProbe(probe, timeout, gate);
            }
        }
    }

    private static void ObserveProbe(Task probe, CancellationTokenSource timeout, SemaphoreSlim gate)
    {
        _ = probe.ContinueWith(
            static (completed, state) =>
            {
                var (source, semaphore) = ((CancellationTokenSource Cts, SemaphoreSlim Gate))state!;
                if (completed.IsFaulted)
                {
                    _ = completed.Exception;
                }

                source.Dispose();
                semaphore.Release();
            },
            (Cts: timeout, Gate: gate),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
