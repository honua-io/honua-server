// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Domain;

namespace Honua.Core.Features.Grounding.Abstractions;

public interface IGroundingEngine
{
    string Name { get; }

    // Classification must respect GroundingRequest.WorkflowFamilyHint when the caller set one.
    WorkflowFamilyClassification Classify(GroundingRequest request);

    // Callers pass the catalog snapshot so the engine stays pure.
    IReadOnlyList<GroundingCandidate> ScoreProcesses(
        GroundingRequest request,
        IReadOnlyList<ProcessDefinition> processes);

    IReadOnlyList<GroundingCandidate> ScoreLayers(
        GroundingRequest request,
        IReadOnlyList<LayerCandidate> layers);

    IReadOnlyList<GroundingCandidate> ScoreServices(
        GroundingRequest request,
        IReadOnlyList<ServiceCandidate> services);
}

// This record stays free of the catalog assembly so the engine does not take that dependency.
public sealed record LayerCandidate(int Id, string Name, string? Description);

public sealed record ServiceCandidate(string Name, string? Description);
