// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

public sealed record CoordinatedReleaseHumanApproval
{
    public const int CurrentVersion = 1;

    public int Version { get; init; }
    public required string WorkflowId { get; init; }
    public required long WorkflowVersion { get; init; }
    public required CoordinatedReleaseStep Gate { get; init; }
    // Not required: control-plane records omit nulls on write, and an install without tenancy has no
    // tenant, so a required property would refuse every stored approval on read.
    public string? TenantId { get; init; }
    public required string ActorId { get; init; }
    public required string Channel { get; init; }
    public required string SessionReference { get; init; }
    public required string CapabilityReference { get; init; }
    public required string PlanHash { get; init; }
    public required DateTimeOffset ApprovedAt { get; init; }
}
