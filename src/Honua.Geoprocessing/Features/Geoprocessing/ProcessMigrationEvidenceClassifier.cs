// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Classifies built-in processes for migration evidence claims.
/// </summary>
internal static class ProcessMigrationEvidenceClassifier
{
    private static readonly FrozenSet<string> FirstSliceAutomatedVectorProcessIds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "geometry.buffer",
            "geometry.clip",
            "geometry.intersect",
            "geometry.project",
            "geometry.simplify",
            "geometry.snap",
            "geometry.dissolve",
            "geometry.make-valid",
            "geometry.union",
            "geometry.difference",
            "geometry.area",
            "geometry.length",
            "geometry.centroid",
            "geometry.convex-hull",
            "analytics.buffer-aggregate",
            "analytics.spatial-join",
            "analytics.density",
            "analytics.cluster",
            "conversion.feature-project",
            "generalization.simplify-layer",
            "generalization.dissolve",
        }.ToFrozenSet(StringComparer.Ordinal);

    public static ProcessMigrationEvidenceClassification Classify(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var processId = definition.ProcessId;
        if (FirstSliceAutomatedVectorProcessIds.Contains(processId))
        {
            return new ProcessMigrationEvidenceClassification(
                ProcessMigrationAutomationTier.Automated,
                RequiresApproval: false,
                IsProjectedThroughOgcApiProcesses: true,
                "First-slice deterministic vector process evidence.");
        }

        if (ProcessDestructiveClassifier.IsDestructive(processId))
        {
            return new ProcessMigrationEvidenceClassification(
                ProcessMigrationAutomationTier.ManualReview,
                RequiresApproval: true,
                IsProjectedThroughOgcApiProcesses: false,
                "Destructive data-management process; execution requires operator approval.");
        }

        return definition.Category switch
        {
            "surface" or "raster" => new ProcessMigrationEvidenceClassification(
                ProcessMigrationAutomationTier.Assisted,
                RequiresApproval: false,
                IsProjectedThroughOgcApiProcesses: false,
                "Heavyweight raster/surface family; native-profile execution routed to the GDAL worker image, validation-only in the lean image."),

            "conversion" when definition.OutputArtifactKinds.Contains(ArtifactKind.Raster) => new ProcessMigrationEvidenceClassification(
                ProcessMigrationAutomationTier.Assisted,
                RequiresApproval: false,
                IsProjectedThroughOgcApiProcesses: false,
                "Heavyweight raster conversion family; catalog and validation only in this evidence slice."),

            "data-management" => new ProcessMigrationEvidenceClassification(
                ProcessMigrationAutomationTier.Assisted,
                RequiresApproval: false,
                IsProjectedThroughOgcApiProcesses: false,
                "Data-management process is inventoried for migration review, not projected as automated runtime evidence."),

            _ => new ProcessMigrationEvidenceClassification(
                ProcessMigrationAutomationTier.Unsupported,
                RequiresApproval: false,
                IsProjectedThroughOgcApiProcesses: false,
                "Not included in the first process migration evidence slice.")
        };
    }

    public static bool IsFirstSliceOgcProcess(ProcessDefinition definition)
        => Classify(definition).IsProjectedThroughOgcApiProcesses;
}

internal sealed record ProcessMigrationEvidenceClassification(
    ProcessMigrationAutomationTier AutomationTier,
    bool RequiresApproval,
    bool IsProjectedThroughOgcApiProcesses,
    string EvidencePosture);

internal enum ProcessMigrationAutomationTier
{
    Automated,
    Assisted,
    ManualReview,
    Unsupported
}
