// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Orchestration.Domain;

public sealed record WorkflowDefinition
{
    public required string WorkflowId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    // The step list must be non-empty and free of cycles.
    public required IReadOnlyList<WorkflowStepDefinition> Steps { get; init; }

    public WorkflowTrigger? Trigger { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    // A triggered run inherits the publisher's row and field security snapshot; a definition without one is refused rather than run as the admin orchestrator.
    public JobSecurityContext? AuthorSecurityContext { get; init; }
}
