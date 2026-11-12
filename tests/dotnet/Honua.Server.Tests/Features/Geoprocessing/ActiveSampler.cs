// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Tests.Features.Geoprocessing;

// Records the highest number (and cost) of active execution records seen globally and per
// partition while concurrent admission tests run.
internal sealed class ActiveSampler(
    Func<ExecutionJobRecord, string> partitionKey,
    Func<ExecutionJobRecord, double> cost)
{
    private readonly object _sync = new();

    public int Samples { get; private set; }

    public int MaxGlobal { get; private set; }

    public Dictionary<string, int> MaxPerPartition { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, double> MaxCostPerPartition { get; } = new(StringComparer.Ordinal);

    public async Task RunAsync(IExecutionJobStore store, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await SampleAsync(store);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task SampleAsync(IExecutionJobStore store)
    {
        var active = await store.ListActiveAsync();
        lock (_sync)
        {
            Samples++;
            MaxGlobal = Math.Max(MaxGlobal, active.Count);
            foreach (var partition in active.GroupBy(partitionKey))
            {
                MaxPerPartition[partition.Key] = Math.Max(
                    MaxPerPartition.GetValueOrDefault(partition.Key), partition.Count());
                MaxCostPerPartition[partition.Key] = Math.Max(
                    MaxCostPerPartition.GetValueOrDefault(partition.Key), partition.Sum(cost));
            }
        }
    }
}
