// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
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
}
