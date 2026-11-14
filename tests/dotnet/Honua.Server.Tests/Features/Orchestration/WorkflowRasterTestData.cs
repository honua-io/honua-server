// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Server.Tests.Features.Orchestration;

internal static class WorkflowRasterTestData
{
    internal static AnalysisPlan Plan() => new()
    {
        PlanId = "raster-plan",
        IntentId = "raster-intent",
        Steps = [new AnalysisPlanStep
        {
            StepId = "clip",
            Kind = AnalysisPlanStepKind.Geoprocess,
            ProcessId = "raster.clip",
            RasterSources = new Dictionary<string, RasterSourceDescriptor>
            {
                ["source"] = new PostgisRasterSourceDescriptor
                {
                    LayerId = 1,
                    RasterId = 2,
                    Version = "v1",
                    Content = new RasterContentIdentity { SizeBytes = 4096, MediaType = "image/tiff" },
                    SecurityContext = new RasterSecurityContextReference
                    {
                        TenantId = "tenant",
                        AuthorizationSnapshotReference = "authorized-context"
                    }
                }
            }
        }]
    };
}
