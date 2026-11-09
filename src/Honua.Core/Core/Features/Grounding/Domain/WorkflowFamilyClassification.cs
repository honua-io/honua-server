// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Grounding.Domain;

public sealed record WorkflowFamilyClassification
{
    public required WorkflowFamily Value { get; init; }

    public required double Confidence { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = [];
}
