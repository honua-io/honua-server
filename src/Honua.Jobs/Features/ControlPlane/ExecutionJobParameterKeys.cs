// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.ControlPlane;

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
    /// Stable job definition identifier used by Console and Operate deep links.
    /// </summary>
    public const string DefinitionId = "honua.job.definition_id";

    /// <summary>
    /// Queue or routing lane requested for the job.
    /// </summary>
    public const string Queue = "honua.job.queue";

    /// <summary>
    /// Pipe-delimited resource references related to the job.
    /// </summary>
    public const string ResourceRefs = "honua.job.resource_refs";

    /// <summary>
    /// Parent job identifier when this job is part of a larger workflow.
    /// </summary>
    public const string ParentId = "honua.job.parent_id";

    /// <summary>
    /// Pipe-delimited child job identifiers when this job fans out work.
    /// </summary>
    public const string ChildIds = "honua.job.child_ids";

    /// <summary>
    /// Runtime or deployment environment associated with the job.
    /// </summary>
    public const string Environment = "honua.job.environment";

    /// <summary>
    /// Server or worker instance associated with the job.
    /// </summary>
    public const string Server = "honua.job.server";

    /// <summary>
    /// Release identifier associated with the job.
    /// </summary>
    public const string ReleaseId = "honua.job.release_id";

    /// <summary>
    /// Change-set identifier associated with the job.
    /// </summary>
    public const string ChangeSetId = "honua.job.change_set_id";

    /// <summary>
    /// Alert identifier associated with the job.
    /// </summary>
    public const string AlertId = "honua.job.alert_id";

    /// <summary>
    /// Distributed trace identifier associated with the job.
    /// </summary>
    public const string TraceId = "honua.trace_id";

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
    /// Scheduled Share export definition identifier.
    /// </summary>
    public const string ShareExportId = "honua.share.export_id";

    /// <summary>
    /// Scheduled Share export run identifier.
    /// </summary>
    public const string ShareRunId = "honua.share.run_id";

    /// <summary>
    /// Scheduled Share export destination type.
    /// </summary>
    public const string ShareDestinationType = "honua.share.destination_type";

    /// <summary>
    /// Scheduled Share export output format.
    /// </summary>
    public const string ShareFormat = "honua.share.format";

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
