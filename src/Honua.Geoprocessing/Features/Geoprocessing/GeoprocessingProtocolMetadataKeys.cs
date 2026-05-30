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
