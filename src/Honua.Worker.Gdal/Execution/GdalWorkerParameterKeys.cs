// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Worker.Gdal.Execution;

internal static class GdalWorkerParameterKeys
{
    public const string StepInputPrefix = "honua.geoprocessing.step.0.";

    public const string StepRasterSourcePrefix = "honua.geoprocessing.raster_source.0.";

    public const string HydratedStagedSourcePrefix = "honua.worker.gdal.staged_input.0.";

    public const string ProcessDefinitions = "honua.geoprocessing.process_definitions";

    public const string PlanId = "honua.geoprocessing.plan_id";

    public const string GPServerWorkspace = "gpserver.env.workspace";

    public const string GPServerOverwriteOutput = "gpserver.env.overwriteOutput";

    public const string OutputNamePrefix = "process.output.";

    public const string GPServerOutputNamePrefix = "gpserver.output.";

    public const string OutputRegistrationPrefix = "honua.geoprocessing.output_registration.";

    public const string MetadataListSeparator = "|";

    public const string NativeRuntimeProfile = RuntimeProfiles.Native;
}
