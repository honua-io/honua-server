// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Grounding;

internal sealed class GroundingOptions
{
    public const string SectionName = "Operator:Grounding";

    public double WorkflowFamilyFloor { get; set; } = 0.60;

    public double HighConfidenceFloor { get; set; } = 0.70;

    public double MediumConfidenceFloor { get; set; } = 0.40;

    public double MaterialSpread { get; set; } = 0.05;

    public int MaxCandidatesPerKind { get; set; } = 5;
}
