// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Postgres.Tests.Features.Raster;

internal static class PostgisRasterGovernanceTestData
{
    public static RasterProviderExecutionRequest Request(
        string tenantId = "tenant-a",
        RasterCostEstimate? cost = null) => new()
        {
            OperationId = "raster-job-1",
            Attempt = 2,
            TenantId = tenantId,
            Parameters = new Dictionary<string, string>
            {
                [RasterProviderExecutionParameterKeys.TenantId] = tenantId,
            },
            Decision = new RasterExecutionDecision
            {
                ProcessId = "raster.slope",
                Engine = RasterEngine.Postgis,
                ProviderId = "postgis",
                ProviderPolicyVersion = "postgis-raster-v1",
                Placement = RasterExecutionPlacement.DurablePostgis,
                InputResidencies = [RasterInputResidency.Postgis],
                OutputSink = RasterOutputSink.JobArtifact,
                Cost = cost ?? Cost(),
                SemanticVersion = "1.0.0",
                ImplementationVersion = "honua.postgis.raster.slope@1.0.0",
                ReasonCode = "postgis-source-local",
                Reason = "test",
                PolicyRef = "raster-default",
                ConfigurationVersion = "raster-execution-v1",
                HealthVersion = "health-v1",
            },
        };

    public static RasterCostEstimate Cost() => new()
    {
        ProcessId = "raster.slope",
        Engine = RasterEngine.Postgis,
        SourceCount = 1,
        BandCount = 1,
        ZoneCount = 0,
        InputPixels = 256,
        OutputPixels = 256,
        DecodedBytes = 1024,
        ExpectedScratchBytes = 1024,
        ExpectedDatabaseWork = 256,
        UnknownInputs = [],
        RequestExecutionAllowed = false,
    };
}
