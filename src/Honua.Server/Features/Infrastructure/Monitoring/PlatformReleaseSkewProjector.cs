// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Infrastructure.Monitoring;

internal static class PlatformReleaseSkewProjector
{
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
