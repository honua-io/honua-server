// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Admin.TileOperations;

internal sealed class TileCacheJobTerminalCallback : IJobTerminalCallback
{
    public ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        if (job.Spec.Kind == ExecutionJobKind.TileCache)
        {
            TileOperationMetrics.RecordJobEnded(job);
        }

        return ValueTask.CompletedTask;
    }
}
