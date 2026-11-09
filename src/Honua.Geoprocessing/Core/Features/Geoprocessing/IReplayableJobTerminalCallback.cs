// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Geoprocessing;

internal interface IReplayableJobTerminalCallback : IJobTerminalCallback
{
    ValueTask ReplayAsync(ExecutionJobRecord job, CancellationToken cancellationToken);
}
