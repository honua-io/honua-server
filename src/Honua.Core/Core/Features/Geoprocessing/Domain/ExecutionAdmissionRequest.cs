// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record ExecutionAdmissionRequest
{
    public required ExecutionJobKind JobKind { get; init; }

    public string? PartitionKey { get; init; }

    public string? PrincipalId { get; init; }

    public double? EstimatedCostWeight { get; init; }

    public OperationPriority Priority { get; init; } = OperationPriority.Normal;
}
