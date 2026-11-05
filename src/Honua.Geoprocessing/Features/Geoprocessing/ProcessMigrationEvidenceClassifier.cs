// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;

namespace Honua.Geoprocessing;

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
            // Async batch enrichment (#2283): a managed-NTS vector op over the shared
            // spatial-join computation, projected through OGC API Processes so the
            // sync /api/enrich over-limit path has a first-class async counterpart.
            "enrichment.enrich",
        }.ToFrozenSet(StringComparer.Ordinal);

    // Single shared registry instance built from the same descriptor set the DI
    // container seeds, so the static GP classifier resolves identical verdicts.
    private static readonly IEsriConstructCapabilityRegistry Registry =
        new GeoServicesConstructCapabilityRegistry(GeoServicesConstructCapabilityRegistry.BuiltInDescriptors);

    public static ProcessMigrationEvidenceClassification Classify(ProcessDefinition definition)
        => Classify(definition, Registry);

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
            GeoServicesConstructCapabilityRegistry.Keys.GpDestructiveDataManagement,
            StringComparison.Ordinal);

        return new ProcessMigrationEvidenceClassification(
            tier,
            RequiresApproval: requiresApproval,
            IsProjectedThroughOgcApiProcesses: descriptor.CanServe,
            descriptor.Reason);
    }

    private static string ResolveConstructKey(ProcessDefinition definition)
    {
        var processId = definition.ProcessId;
        if (FirstSliceAutomatedVectorProcessIds.Contains(processId))
        {
            return GeoServicesConstructCapabilityRegistry.Keys.GpVectorDeterministic;
        }

        if (ProcessDestructiveClassifier.IsDestructive(processId))
        {
            return GeoServicesConstructCapabilityRegistry.Keys.GpDestructiveDataManagement;
        }

        return definition.Category switch
        {
            "surface" or "raster" => GeoServicesConstructCapabilityRegistry.Keys.GpRasterSurface,
            "imagery" => GeoServicesConstructCapabilityRegistry.Keys.GpImageryInference,
            "conversion" when definition.OutputArtifactKinds.Contains(ArtifactKind.Raster) =>
                GeoServicesConstructCapabilityRegistry.Keys.GpRasterConversion,
            "data-management" => GeoServicesConstructCapabilityRegistry.Keys.GpDataManagement,
            _ => GeoServicesConstructCapabilityRegistry.Keys.GpUnsupported
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
