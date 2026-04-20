// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Domain;

namespace Honua.Core.Features.Grounding.Abstractions;

/// <summary>
/// Pluggable ranker + classifier behind <see cref="IGroundingService"/>. The
/// service handles authorization filtering, provenance wiring, draft-intent
/// shaping, and clarification emission; the engine is responsible only for
/// producing candidate scores and a workflow-family classification.
///
/// The default <c>DeterministicGroundingEngine</c> ships with the service.
/// Model-backed engines (e.g. embeddings rerankers) can register themselves
/// through the same interface without contract churn.
/// </summary>
public interface IGroundingEngine
{
    /// <summary>
    /// Short engine identifier emitted in telemetry and conformance fixtures.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Classifies the request into a workflow family. Must respect
    /// <see cref="GroundingRequest.WorkflowFamilyHint"/> when present.
    /// </summary>
    WorkflowFamilyClassification Classify(GroundingRequest request);

    /// <summary>
    /// Scores every process in the supplied snapshot against the request.
    /// Callers pass the catalog snapshot to keep the engine pure.
    /// </summary>
    IReadOnlyList<GroundingCandidate> ScoreProcesses(
        GroundingRequest request,
        IReadOnlyList<ProcessDefinition> processes);

    /// <summary>
    /// Scores layer candidates against the request.
    /// </summary>
    IReadOnlyList<GroundingCandidate> ScoreLayers(
        GroundingRequest request,
        IReadOnlyList<LayerCandidate> layers);

    /// <summary>
    /// Scores service catalog candidates against the request.
    /// </summary>
    IReadOnlyList<GroundingCandidate> ScoreServices(
        GroundingRequest request,
        IReadOnlyList<ServiceCandidate> services);
}

/// <summary>
/// Pared-down layer record fed to the engine. Keeps the engine independent of
/// the <c>Honua.Core.Features.Catalog</c> assembly while still carrying the
/// fields ranking needs.
/// </summary>
public sealed record LayerCandidate(int Id, string Name, string? Description);

/// <summary>
/// Pared-down service record fed to the engine.
/// </summary>
public sealed record ServiceCandidate(string Name, string? Description);
