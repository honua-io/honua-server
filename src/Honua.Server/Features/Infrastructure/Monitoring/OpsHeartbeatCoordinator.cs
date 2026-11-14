// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Observability.Abstractions;

namespace Honua.Server.Features.Infrastructure.Monitoring;

internal sealed class OpsHeartbeatCoordinator(IExecutionJobStore jobStore, IOpsHeartbeatPublisher publisher)
{
    public Task PublishAsync(string stackName, CancellationToken cancellationToken)
    {
        _ = jobStore;
        _ = publisher;
        return Task.CompletedTask;
    }
}
