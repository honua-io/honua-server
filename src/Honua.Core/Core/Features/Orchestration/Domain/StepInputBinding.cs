// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Orchestration.Domain;

public sealed record StepInputBinding
{
    public required string SourceStepId { get; init; }

    public required string SourceArtifactSelector { get; init; }

    public required string TargetInputKey { get; init; }
}
