// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Projects the configured control-plane deploy targets and execution workloads against the declared
/// platform release into a co-versioning skew snapshot. Shared by the ops-health composer and the
/// ops-findings platform-release-skew rule so both derive skew identically from configuration.
/// </summary>
internal static class PlatformReleaseSkewProjector
{
    /// <summary>
    /// Builds the platform-release skew snapshot from the current control-plane options.
    /// </summary>
    /// <param name="options">The control-plane options carrying the release, deploy targets, and workloads.</param>
    /// <returns>The skew snapshot (release version, co-versioning flag, and skewed plane ids).</returns>
    public static PlatformReleaseSkewSnapshot Build(ControlPlaneOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var servingEntries = BuildEntries(
            options.DeployTargets.Select(target => (target.TargetId, target.RuntimeProfile, target.ArtifactReference)));
        var executionEntries = BuildEntries(
            options.ExecutionWorkloads.Select(workload => (workload.WorkloadId, workload.RuntimeProfile, workload.ArtifactReference)));

        return PlatformReleaseProjection.BuildSkewSnapshot(
            options.PlatformRelease.ToDefinition(),
            servingEntries,
            executionEntries);
    }

    private static PlatformReleasePlaneEntry[] BuildEntries(
        IEnumerable<(string Id, string? RuntimeProfile, string? ArtifactReference)> entries)
        => entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
            .Select(entry => new PlatformReleasePlaneEntry
            {
                Id = entry.Id,
                RuntimeProfile = entry.RuntimeProfile,
                ExplicitArtifactReference = entry.ArtifactReference,
            })
            .ToArray();
}
