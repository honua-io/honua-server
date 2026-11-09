// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Deployment.Domain;

public sealed record DeploymentTransition
{
    public required DeploymentStatus From { get; init; }

    public required DeploymentStatus To { get; init; }

    public required DateTimeOffset At { get; init; }

    public RolloutState? RolloutState { get; init; }

    public string? Reason { get; init; }

    public OperationAuditInfo Audit { get; init; } = new();
}
