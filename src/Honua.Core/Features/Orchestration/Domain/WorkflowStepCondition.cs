// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Orchestration.Domain;

// Every predicate is a pure check of already-captured state, so a branch decision is reproducible without expression parsing.
public enum WorkflowStepConditionKind
{
    UpstreamSucceeded,

    UpstreamSkipped,

    HasArtifact,

    NoArtifact,

    ArtifactCountAtLeast,

    ArtifactCountAtMost,
}

// When the predicate is not met the step is skipped rather than submitted.
public sealed record WorkflowStepCondition(
    string SourceStepId,
    WorkflowStepConditionKind Kind,
    int Threshold = 0,
    string? ArtifactLabel = null,
    bool Negate = false);

public static class WorkflowStepConditionEvaluator
{
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

        return artifacts.Count(artifact => string.Equals(artifact.Label, label, StringComparison.OrdinalIgnoreCase));
    }
}
