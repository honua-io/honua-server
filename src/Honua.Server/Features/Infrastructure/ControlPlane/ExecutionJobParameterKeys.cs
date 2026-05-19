// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Canonical parameter keys stored on execution-job specs when submit paths need
/// to preserve workflow metadata for reconciliation and progress projection.
/// </summary>
internal static class ExecutionJobParameterKeys
{
    /// <summary>
    /// Separator used when serializing ordered metadata lists into a single spec parameter value.
    /// </summary>
    public const string MetadataListSeparator = "|";

    /// <summary>
    /// Stable analysis plan identifier for geoprocessing execution jobs.
    /// </summary>
    public const string GeoprocessingPlanId = "honua.geoprocessing.plan_id";

    /// <summary>
    /// Ordered process identifiers referenced by the submitted analysis plan.
    /// </summary>
    public const string GeoprocessingProcessDefinitions = "honua.geoprocessing.process_definitions";

    /// <summary>
    /// Ordered output artifact kinds declared by the submitted analysis plan.
    /// </summary>
    public const string GeoprocessingOutputArtifactKinds = "honua.geoprocessing.output_artifact_kinds";

    /// <summary>
    /// Prefix for canonical step-input parameters projected onto the job spec by
    /// the geoprocessing submit path. The first-slice production executors
    /// (e.g. <c>geometry.buffer</c>) read their parameters from this prefix because
    /// the durable spec is the only payload available to worker-side dispatch. The
    /// prefix encodes the step ordinal so a future multi-step plan does not require
    /// a separate substrate.
    /// </summary>
    public const string GeoprocessingStepInputPrefix = "honua.geoprocessing.step.";
}
