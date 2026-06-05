// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;

namespace Honua.Geoprocessing;

/// <summary>
/// Classifies built-in processes for migration evidence claims.
/// </summary>
/// <remarks>
/// The capability/fidelity verdict (automation tier) is sourced from the shared
/// <see cref="IEsriConstructCapabilityRegistry"/> (#1382): the classifier maps a
/// process onto a GP construct family key and the registry supplies the verdict,
/// keeping the GP lane aligned with the importer and Portal-facade lanes rather
/// than maintaining a private tier table. Unrecognised keys fall back to the
/// shared <c>manual-review</c> descriptor.
/// </remarks>
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

    // Single shared registry instance built from the same descriptor set the DI
    // container seeds, so the static GP classifier resolves identical verdicts.
    private static readonly IEsriConstructCapabilityRegistry Registry =
        new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors);

    public static ProcessMigrationEvidenceClassification Classify(ProcessDefinition definition)
        => Classify(definition, Registry);

    /// <summary>
    /// Classifies <paramref name="definition"/> using the supplied capability
    /// registry. The construct-family mapping stays local (it is the lane's
    /// parsing job); the registry owns the per-family capability/fidelity verdict.
    /// </summary>
    public static ProcessMigrationEvidenceClassification Classify(
        ProcessDefinition definition,
        IEsriConstructCapabilityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(registry);

        var constructKey = ResolveConstructKey(definition);
        var descriptor = registry.ResolveOrUnknown(constructKey);

        var tier = ToAutomationTier(descriptor.AutomationStatus);
        var requiresApproval = string.Equals(
            constructKey,
            EsriConstructCapabilityRegistry.Keys.GpDestructiveDataManagement,
            StringComparison.Ordinal);

        return new ProcessMigrationEvidenceClassification(
            tier,
            RequiresApproval: requiresApproval,
            IsProjectedThroughOgcApiProcesses: descriptor.CanServe,
            descriptor.Reason);
    }

    /// <summary>
    /// Maps a process to its GP construct family registry key. Every process maps
    /// to a registered family (including the explicit <c>gp.unsupported</c> catch
    /// for processes outside the first evidence slice), so the shared unknown
    /// fallback only fires when the registry itself lacks the key.
    /// </summary>
    private static string ResolveConstructKey(ProcessDefinition definition)
    {
        var processId = definition.ProcessId;
        if (FirstSliceAutomatedVectorProcessIds.Contains(processId))
        {
            return EsriConstructCapabilityRegistry.Keys.GpVectorDeterministic;
        }

        if (ProcessDestructiveClassifier.IsDestructive(processId))
        {
            return EsriConstructCapabilityRegistry.Keys.GpDestructiveDataManagement;
        }

        return definition.Category switch
        {
            "surface" or "raster" => EsriConstructCapabilityRegistry.Keys.GpRasterSurface,
            "conversion" when definition.OutputArtifactKinds.Contains(ArtifactKind.Raster) =>
                EsriConstructCapabilityRegistry.Keys.GpRasterConversion,
            "data-management" => EsriConstructCapabilityRegistry.Keys.GpDataManagement,
            _ => EsriConstructCapabilityRegistry.Keys.GpUnsupported
        };
    }

    private static ProcessMigrationAutomationTier ToAutomationTier(string automationStatus)
        => automationStatus switch
        {
            MigrationFidelityAutomationStatuses.Automated => ProcessMigrationAutomationTier.Automated,
            MigrationFidelityAutomationStatuses.Assisted => ProcessMigrationAutomationTier.Assisted,
            MigrationFidelityAutomationStatuses.ManualReview => ProcessMigrationAutomationTier.ManualReview,
            _ => ProcessMigrationAutomationTier.Unsupported
        };

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
