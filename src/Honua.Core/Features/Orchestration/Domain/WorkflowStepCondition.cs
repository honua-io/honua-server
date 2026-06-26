// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Orchestration.Domain;

/// <summary>
/// The kind of predicate a <see cref="WorkflowStepCondition"/> applies to a prior
/// step's output. Every kind is a pure, deterministic check over already-captured
/// state so a branch decision is reproducible and AOT-safe (no expression parsing).
/// </summary>
public enum WorkflowStepConditionKind
{
    /// <summary>The source step reached <see cref="WorkflowStepStatus.Succeeded"/>.</summary>
    UpstreamSucceeded,

    /// <summary>The source step reached <see cref="WorkflowStepStatus.Skipped"/>.</summary>
    UpstreamSkipped,

    /// <summary>The source step produced at least one output artifact (optionally matching a label).</summary>
    HasArtifact,

    /// <summary>The source step produced no output artifacts (optionally for a matching label).</summary>
    NoArtifact,

    /// <summary>The source step produced at least <see cref="WorkflowStepCondition.Threshold"/> artifacts.</summary>
    ArtifactCountAtLeast,

    /// <summary>The source step produced at most <see cref="WorkflowStepCondition.Threshold"/> artifacts.</summary>
    ArtifactCountAtMost,
}

/// <summary>
/// A conditional-branch predicate over a single prior step's output. The condition is
/// evaluated once the owning step's dependencies are satisfied; when it is not met the
/// owning step is skipped (and reported as such) rather than submitted, letting a
/// workflow take a data-dependent path without reimplementing dependency resolution.
/// </summary>
/// <param name="SourceStepId">
/// The prior step whose output the predicate reads. It must be declared in the owning
/// step's <see cref="WorkflowStepDefinition.DependsOn"/> so it is always terminal
/// before the predicate evaluates.
/// </param>
/// <param name="Kind">The predicate applied to the source step's output.</param>
/// <param name="Threshold">
/// The operand for the count predicates (<see cref="WorkflowStepConditionKind.ArtifactCountAtLeast"/>
/// and <see cref="WorkflowStepConditionKind.ArtifactCountAtMost"/>); ignored otherwise.
/// </param>
/// <param name="ArtifactLabel">
/// When set, the artifact predicates count only artifacts whose label matches
/// (case-insensitive); otherwise all artifacts count.
/// </param>
/// <param name="Negate">When <see langword="true"/>, the predicate result is inverted.</param>
public sealed record WorkflowStepCondition(
    string SourceStepId,
    WorkflowStepConditionKind Kind,
    int Threshold = 0,
    string? ArtifactLabel = null,
    bool Negate = false);

/// <summary>
/// Pure evaluator for a <see cref="WorkflowStepCondition"/>. Carries no I/O so the
/// branch decision is a deterministic function of the run's current step states; the
/// engine and tests share this single implementation.
/// </summary>
public static class WorkflowStepConditionEvaluator
{
    /// <summary>
    /// Evaluates the condition against the current step states. When the source step is
    /// unknown (should not happen for a validated definition) the predicate is treated
    /// as not met so the owning step is skipped rather than run on missing data.
    /// </summary>
    /// <param name="condition">The branch predicate.</param>
    /// <param name="statesByStepId">The run's current step states, keyed by step id.</param>
    /// <returns><see langword="true"/> when the branch should be taken.</returns>
    public static bool Evaluate(
        WorkflowStepCondition condition,
        IReadOnlyDictionary<string, WorkflowStepState> statesByStepId)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(statesByStepId);

        if (!statesByStepId.TryGetValue(condition.SourceStepId, out var source))
        {
            return false;
        }

        var result = condition.Kind switch
        {
            WorkflowStepConditionKind.UpstreamSucceeded => source.Status == WorkflowStepStatus.Succeeded,
            WorkflowStepConditionKind.UpstreamSkipped => source.Status == WorkflowStepStatus.Skipped,
            WorkflowStepConditionKind.HasArtifact => CountArtifacts(source.OutputArtifacts, condition.ArtifactLabel) >= 1,
            WorkflowStepConditionKind.NoArtifact => CountArtifacts(source.OutputArtifacts, condition.ArtifactLabel) == 0,
            WorkflowStepConditionKind.ArtifactCountAtLeast => CountArtifacts(source.OutputArtifacts, condition.ArtifactLabel) >= condition.Threshold,
            WorkflowStepConditionKind.ArtifactCountAtMost => CountArtifacts(source.OutputArtifacts, condition.ArtifactLabel) <= condition.Threshold,
            _ => false
        };

        return condition.Negate ? !result : result;
    }

    private static int CountArtifacts(IReadOnlyList<ArtifactRef>? artifacts, string? label)
    {
        if (artifacts is null || artifacts.Count == 0)
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            return artifacts.Count;
        }

        var count = 0;
        foreach (var artifact in artifacts)
        {
            if (string.Equals(artifact.Label, label, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }
}
