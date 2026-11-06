// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Publishing.Domain;

namespace Honua.Core.Features.Grounding.Domain;

public sealed record DraftIntent
{
    public required string IntentId { get; init; }

    public required string Goal { get; init; }

    public required WorkflowFamily WorkflowFamily { get; init; }

    public IReadOnlyList<ArtifactKind> RequestedOutputs { get; init; } = [];

    public AssumptionPolicy AssumptionPolicy { get; init; } = AssumptionPolicy.AskWhenMaterial;

    public AnalysisIntent? Analysis { get; init; }

    public PublishIntent? Publishing { get; init; }

    public required ProvenanceRecord Provenance { get; init; }
}
