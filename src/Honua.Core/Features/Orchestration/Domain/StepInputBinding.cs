// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Orchestration.Domain;

/// <summary>
/// Wires a single upstream step output into a downstream step's input dictionary.
/// </summary>
public sealed record StepInputBinding
{
    /// <summary>
    /// Step identifier within the same workflow that produces the artifact.
    /// </summary>
    public required string SourceStepId { get; init; }

    /// <summary>
    /// Selector that resolves an artifact from the upstream step's result package.
    /// Supported forms are <c>artifact:{index}</c> and <c>artifact:{label}</c> where label
    /// matches <see cref="Geoprocessing.Domain.ArtifactRef.Label"/> case-insensitively.
    /// </summary>
    public required string SourceArtifactSelector { get; init; }

    /// <summary>
    /// Key inserted into the downstream step's <see cref="Geoprocessing.Domain.AnalysisPlanStep.Inputs"/>.
    /// </summary>
    public required string TargetInputKey { get; init; }
}
