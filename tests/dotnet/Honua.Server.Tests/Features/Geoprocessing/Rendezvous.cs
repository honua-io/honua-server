// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.Server.Tests.Features.Geoprocessing;

// Holds every wrapped node's record creation until `parties` nodes have reached it (or the
// timeout passes), forcing the evaluations to complete before any record becomes visible.
internal sealed class Rendezvous(int parties, TimeSpan timeout)
{
    private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public bool AllArrived => _allArrived.Task.IsCompleted;

    public InterceptingJobStore Wrap(IExecutionJobStore inner) => new(inner)
    {
        BeforeCreate = ArriveAsync
    };

    private async Task ArriveAsync()
    {
        if (Interlocked.Increment(ref _arrived) >= parties)
        {
            _allArrived.TrySetResult();
        }

        await _allArrived.Task.WaitAsync(timeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
}
