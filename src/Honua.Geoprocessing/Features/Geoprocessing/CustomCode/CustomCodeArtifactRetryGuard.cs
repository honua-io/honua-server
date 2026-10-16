// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Geoprocessing.CustomCode;

/// <summary>Cumulative storage ceilings, including journals and failed reservations across retries.</summary>
internal static class CustomCodeArtifactQuota
{
    internal const long MaximumArtifactBytes = 64L * 1024 * 1024;
    internal const long MaximumJobBytes = 256L * 1024 * 1024;
    internal const long MaximumJournalBytes = 1024 * 1024;
    internal const int MaximumArtifacts = 32;
}

/// <summary>Retains charged broker receipts and refuses retries that cannot publish their mandatory journal.</summary>
internal sealed class CustomCodeArtifactRetryGuard : IExecutionJobRetryGuard
{
    public ExecutionJobRetryDecision? EvaluateRetry(ExecutionJobRecord job)
    {
        if (job.Spec.Kind != ExecutionJobKind.Geoprocessing || job.Spec.RuntimeProfile != CustomCodeJobContract.RuntimeProfile)
        {
            return null;
        }

        var retained = new List<string>();
        var identities = new HashSet<(int Attempt, string Name)>();
        long bytes = 0;
        foreach (var reference in job.ArtifactReferences)
        {
            if (!CustomCodeArtifactDescriptor.TryParse(reference, out var artifact))
            {
                if (reference.Contains("honua.gp.custom-artifact/v1", StringComparison.Ordinal))
                {
                    return Refuse(job.ArtifactReferences, "Custom-code retry refused because an artifact reservation is invalid.");
                }

                continue;
            }

            retained.Add(reference);
            if (artifact!.JobId != job.OperationId || artifact.Attempt > job.AttemptCount
                || artifact.SizeBytes > CustomCodeArtifactQuota.MaximumArtifactBytes
                || !identities.Add((artifact.Attempt, artifact.Name)))
            {
                return Refuse(job.ArtifactReferences, "Custom-code retry refused because an artifact reservation is invalid.");
            }

            bytes += artifact.SizeBytes;
            if (retained.Count >= CustomCodeArtifactQuota.MaximumArtifacts
                || bytes > CustomCodeArtifactQuota.MaximumJobBytes - CustomCodeArtifactQuota.MaximumJournalBytes)
            {
                return Refuse(job.ArtifactReferences, "Custom-code retry refused: cumulative artifact capacity cannot reserve the next attempt's mandatory journal.");
            }
        }

        return new ExecutionJobRetryDecision { Allowed = true, ArtifactReferences = retained };
    }

    private static ExecutionJobRetryDecision Refuse(IReadOnlyList<string> retained, string reason)
        => new() { Allowed = false, ArtifactReferences = retained, RefusalReason = reason };
}
