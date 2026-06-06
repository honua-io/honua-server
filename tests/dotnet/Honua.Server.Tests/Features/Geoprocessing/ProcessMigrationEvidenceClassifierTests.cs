// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Contract tests for the first process migration evidence slice.
/// </summary>
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class ProcessMigrationEvidenceClassifierTests
{
    private readonly BuiltInProcessCatalog _catalog = new();

    [UnitTest]
    [Operation(Operations.ProcessDiscovery)]
    public void FirstSlice_VectorProcesses_AreAutomatedAndProjected()
    {
        string[] processIds =
        [
            "geometry.buffer",
            "geometry.clip",
            "geometry.intersect",
            "geometry.project",
            "analytics.buffer-aggregate",
            "analytics.spatial-join",
            "conversion.feature-project",
            "generalization.dissolve"
        ];

        foreach (var processId in processIds)
        {
            var classification = ProcessMigrationEvidenceClassifier.Classify(_catalog.GetProcess(processId)!);

            classification.AutomationTier.Should().Be(ProcessMigrationAutomationTier.Automated);
            classification.RequiresApproval.Should().BeFalse();
            classification.IsProjectedThroughOgcApiProcesses.Should().BeTrue();
        }
    }

    [UnitTest]
    [Operation(Operations.ProcessExecution)]
    public void HeavyweightRasterAndSurfaceProcesses_AreAssistedCatalogOnly()
    {
        string[] processIds = ["surface.slope", "surface.hillshade", "raster.clip", "raster.zonal-statistics"];

        foreach (var processId in processIds)
        {
            var classification = ProcessMigrationEvidenceClassifier.Classify(_catalog.GetProcess(processId)!);

            classification.AutomationTier.Should().Be(ProcessMigrationAutomationTier.Assisted);
            classification.RequiresApproval.Should().BeFalse();
            classification.IsProjectedThroughOgcApiProcesses.Should().BeFalse();
        }
    }

    [UnitTest]
    [Operation(Operations.ProcessExecution)]
    public void DestructiveDataManagementProcesses_RequireManualApproval()
    {
        string[] processIds = ["data-management.delete-features", "data-management.calculate-field"];

        foreach (var processId in processIds)
        {
            var classification = ProcessMigrationEvidenceClassifier.Classify(_catalog.GetProcess(processId)!);

            classification.AutomationTier.Should().Be(ProcessMigrationAutomationTier.ManualReview);
            classification.RequiresApproval.Should().BeTrue();
            classification.IsProjectedThroughOgcApiProcesses.Should().BeFalse();
        }
    }

    [UnitTest]
    [Operation(Operations.ProcessDiscovery)]
    public void Classify_ReadsVerdictFromInjectedRegistry()
    {
        // The GP lane sources the automation verdict from the shared capability
        // registry (#1382). A registry whose deterministic-vector descriptor is
        // overridden to manual-review must flip the lane's verdict — proving the
        // verdict comes from the registry, not a private tier table.
        var overridden = new EsriConstructCapabilityRegistry(
        [
            new EsriConstructCapabilityDescriptor
            {
                ConstructKey = EsriConstructCapabilityRegistry.Keys.GpVectorDeterministic,
                AutomationStatus = MigrationFidelityAutomationStatuses.ManualReview,
                Code = ImportCompatibilityCodes.ManualReview,
                Reason = "Test override: deterministic vector process requires review.",
                CanTransform = false,
                CanServe = false,
                RequiresCheck = true
            }
        ]);

        var classification = ProcessMigrationEvidenceClassifier.Classify(
            _catalog.GetProcess("geometry.buffer")!,
            overridden);

        classification.AutomationTier.Should().Be(ProcessMigrationAutomationTier.ManualReview);
        classification.IsProjectedThroughOgcApiProcesses.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.ProcessDiscovery)]
    public void Classify_BuiltInRegistry_MatchesKnownConstructVerdicts()
    {
        // Behaviour-preservation guard: the built-in registry reproduces the
        // exact verdicts the private table emitted for known GP constructs.
        var registry = new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors);

        registry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.GpVectorDeterministic)
            .AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.Automated);
        registry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.GpDestructiveDataManagement)
            .AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.ManualReview);
        registry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.GpRasterSurface)
            .AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.Assisted);
        registry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.GpRasterConversion)
            .AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.Assisted);
        registry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.GpDataManagement)
            .AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.Assisted);
        registry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.GpUnsupported)
            .AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.Unsupported);
    }

    [UnitTest]
    [Operation(Operations.ProcessDiscovery)]
    public void Classify_RegistryMissingGpKey_FallsBackToManualReview()
    {
        // When the lane maps a process onto a key the registry does not know, the
        // shared UnknownDescriptor manual-review fallback governs the verdict.
        var emptyRegistry = new EsriConstructCapabilityRegistry([]);

        var classification = ProcessMigrationEvidenceClassifier.Classify(
            _catalog.GetProcess("geometry.buffer")!,
            emptyRegistry);

        classification.AutomationTier.Should().Be(ProcessMigrationAutomationTier.ManualReview);
        classification.IsProjectedThroughOgcApiProcesses.Should().BeFalse();
    }
}
