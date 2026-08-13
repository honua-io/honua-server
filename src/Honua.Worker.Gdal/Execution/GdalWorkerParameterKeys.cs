// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Canonical durable-spec parameter keys read by the GDAL worker executors. The
/// submit path projects step inputs onto <c>ExecutionJobSpec.Parameters</c>; the
/// worker reads them back because the durable spec is the only payload available
/// to worker-side dispatch. Keys mirror the lean executors' step-input prefix
/// convention (<c>honua.geoprocessing.step.0.&lt;name&gt;</c>) so a job authored
/// for the lean path is shaped identically for the native path.
/// </summary>
internal static class GdalWorkerParameterKeys
{
    /// <summary>
    /// Step-input prefix matching the lean geoprocessing submit path
    /// (<c>ExecutionJobParameterKeys.GeoprocessingStepInputPrefix</c> + step ordinal 0).
    /// </summary>
    public const string StepInputPrefix = "honua.geoprocessing.step.0.";

    /// <summary>
    /// Typed raster-source prefix matching
    /// <c>ExecutionJobParameterKeys.GeoprocessingStepRasterSourcePrefix</c> for step zero.
    /// </summary>
    public const string StepRasterSourcePrefix = "honua.geoprocessing.raster_source.0.";

    /// <summary>
    /// Worker-internal path prefix populated only after a staged descriptor has been
    /// streamed from the execution-owned store into scratch. It never crosses the
    /// durable server-to-worker contract.
    /// </summary>
    public const string HydratedStagedSourcePrefix = "honua.worker.gdal.staged_input.0.";

    /// <summary>
    /// Parameter key carrying the canonical process id list. Matches
    /// <c>ExecutionJobParameterKeys.GeoprocessingProcessDefinitions</c>.
    /// </summary>
    public const string ProcessDefinitions = "honua.geoprocessing.process_definitions";

    /// <summary>
    /// Stable analysis plan identifier recorded by the submit path
    /// (mirrors <c>ExecutionJobParameterKeys.GeoprocessingPlanId</c>).
    /// </summary>
    public const string PlanId = "honua.geoprocessing.plan_id";

    /// <summary>
    /// Prefix of the per-slot output names recorded by the OGC Processes submit path
    /// (mirrors <c>GeoprocessingProtocolMetadataKeys.OutputNamePrefix</c>).
    /// </summary>
    public const string OutputNamePrefix = "process.output.";

    /// <summary>
    /// Prefix of the per-slot output names recorded by the GPServer submit path
    /// (mirrors <c>GeoprocessingProtocolMetadataKeys.GPServerOutputNamePrefix</c>).
    /// </summary>
    public const string GPServerOutputNamePrefix = "gpserver.output.";

    /// <summary>
    /// Prefix of the optional post-success output registration intents (#3089;
    /// mirrors <c>ExecutionJobParameterKeys.GeoprocessingOutputRegistrationPrefix</c>).
    /// An output carrying an intent must be published as a staged object — the
    /// registrar can only register staged artifacts, so publishing it inline would
    /// permanently wedge the job's results path.
    /// </summary>
    public const string OutputRegistrationPrefix = "honua.geoprocessing.output_registration.";

    /// <summary>
    /// Separator for the ordered process-definitions list.
    /// </summary>
    public const string MetadataListSeparator = "|";

    /// <summary>
    /// The runtime profile claimed by the GDAL worker image. Jobs whose
    /// <c>ExecutionJobSpec.RuntimeProfile</c> equals this value route to the
    /// heavyweight worker; lean managed-profile workers never claim them. Bound
    /// to the canonical <see cref="RuntimeProfiles.Native"/> constant so this
    /// worker and the Core claim-fence helper stay in lock-step.
    /// </summary>
    public const string NativeRuntimeProfile = RuntimeProfiles.Native;
}
