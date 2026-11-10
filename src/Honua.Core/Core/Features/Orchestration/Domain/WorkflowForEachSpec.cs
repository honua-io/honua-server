// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Orchestration.Domain;

// The iteration collection is part of the definition, so unrolling stays deterministic and does not add a cycle.
public sealed record WorkflowForEachSpec(
    IReadOnlyList<string> Items,
    string ItemPlaceholder = "${item}",
    int? MaxIterations = null)
{
    public const int HardIterationLimit = 1000;
}
