// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing;

/// <summary>
/// Shared protocol metadata keys projected onto geoprocessing jobs and artifacts.
/// </summary>
internal static class GeoprocessingProtocolMetadataKeys
{
    /// <summary>
    /// Job spec parameter key storing the GPServer service identifier.
    /// </summary>
    public const string GPServerServiceId = "gpserver.serviceId";

    /// <summary>
    /// Job spec parameter key storing the GPServer task name.
    /// </summary>
    public const string GPServerTaskName = "gpserver.taskName";

    /// <summary>
    /// Job spec parameter key storing the raw GPServer context payload.
    /// </summary>
    public const string GPServerContext = "gpserver.context";

    /// <summary>
    /// Job spec parameter key storing the GP <c>env:outSR</c> output spatial
    /// reference requested by the caller (WKID).
    /// </summary>
    public const string GPServerOutSr = "gpserver.env.outSR";

    /// <summary>
    /// Job spec parameter key storing the GP <c>env:processSR</c> processing
    /// spatial reference requested by the caller (WKID).
    /// </summary>
    public const string GPServerProcessSr = "gpserver.env.processSR";

    /// <summary>
    /// Job spec parameter key storing the GP <c>env:workspace</c> workspace
    /// identifier requested by the caller. Maps onto the existing workspace
    /// lifecycle model (<see cref="Honua.Core.Features.Geoprocessing.Abstractions.IWorkspaceLifecycleService"/>);
    /// tool output artifacts resolve relative to this workspace when set.
    /// </summary>
    public const string GPServerWorkspace = "gpserver.env.workspace";

    /// <summary>
    /// Job spec parameter key storing the GP <c>env:overwriteOutput</c> flag
    /// requested by the caller. Mirrors arcpy's <c>arcpy.env.overwriteOutput</c>
    /// (default <c>False</c>): when absent or <c>false</c>, a write that would
    /// collide with an existing output artifact fails instead of clobbering it.
    /// </summary>
    public const string GPServerOverwriteOutput = "gpserver.env.overwriteOutput";

    /// <summary>
    /// Prefix for stable protocol output parameter names stored on the job spec.
    /// </summary>
    public const string OutputNamePrefix = "process.output.";

    /// <summary>
    /// Prefix for legacy GPServer output parameter names stored on the job spec.
    /// </summary>
    public const string GPServerOutputNamePrefix = "gpserver.output.";

    /// <summary>
    /// Artifact metadata key storing the published GeoServices output parameter name.
    /// </summary>
    public const string GeoServicesOutputParameterMetadataKey = "geoservices.output_parameter";
}
