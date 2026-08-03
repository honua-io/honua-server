// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class PlanLayerReferencesTests
{
    private readonly BuiltInProcessCatalog _catalog = new();

    [UnitTest]
    public void Derive_ImportDatasetVectorSource_DoesNotAuthorizeUnusedRasterTarget()
    {
        var references = PlanLayerReferences.Derive(
            ImportPlan("parcels.geojson", rasterLayerId: "42"),
            _catalog);

        references.Should().BeEmpty();
    }

    [UnitTest]
    public void Derive_ImportDatasetZeroRasterTarget_DoesNotAuthorizeUnusedRasterTarget()
    {
        var references = PlanLayerReferences.Derive(
            ImportPlan("imagery.tif", rasterLayerId: "0"),
            _catalog);

        references.Should().BeEmpty();
    }

    [UnitTest]
    public void Derive_ImportDatasetSupportedRasterSource_AuthorizesInsertTarget()
    {
        var references = PlanLayerReferences.Derive(
            ImportPlan("imagery.TIFF", rasterLayerId: "42"),
            _catalog);

        references.Should().ContainSingle().Which.Should().Be(new PlanLayerReference(
            "step-import",
            "import.dataset",
            "rasterLayerId",
            42,
            ProcessLayerAccess.Insert));
    }

    private static AnalysisPlan ImportPlan(string fileName, string rasterLayerId) => new()
    {
        PlanId = "plan-import",
        IntentId = "intent-import",
        Steps =
        [
            new AnalysisPlanStep
            {
                StepId = "step-import",
                Kind = AnalysisPlanStepKind.Geoprocess,
                ProcessId = "import.dataset",
                Inputs = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["fileName"] = fileName,
                    ["rasterLayerId"] = rasterLayerId,
                },
            },
        ],
    };
}
