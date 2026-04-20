// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Grounding.Domain;

/// <summary>
/// Output of the workflow-family classifier: the chosen family plus a
/// deterministic confidence score in [0,1].
/// </summary>
public sealed record WorkflowFamilyClassification
{
    /// <summary>
    /// The selected workflow family.
    /// </summary>
    public required WorkflowFamily Value { get; init; }

    /// <summary>
    /// Classifier confidence in the selection, in [0,1]. Drives the
    /// <see cref="Geoprocessing.Domain.ClarificationReasonCode.LowConfidence"/>
    /// threshold check.
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// Evidence tags describing why this family won (e.g. "hint", "verb:publish").
    /// </summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];
}
