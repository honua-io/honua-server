// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Admin.TileOperations;

/// <summary>
/// Config-driven backend selection for tile-cache / PMTiles execution jobs
/// (issue #1697). When <see cref="Enabled"/> is set the tile-operation admin path
/// submits seed/warm/invalidate/purge/archive/publish work as durable
/// <see cref="ExecutionJobKind.TileCache"/> execution jobs and dispatches them to
/// the configured <see cref="Backend"/>. When disabled (the default) the legacy
/// in-process channel worker handles tile operations exactly as before, preserving
/// current single-pod behavior.
/// <para>
/// Backend selection mirrors the geoprocessing path: <see cref="Backend"/> chooses
/// among the registered <c>IBatchComputeBackend</c> implementations (for example
/// <c>local</c>, <c>honua-aws-batch</c>, or the Kubernetes Job backend), and
/// <see cref="Parameters"/> carries backend-specific coordinates (AWS Batch job
/// definition / queue ARNs, container resource overrides, target artifact bucket)
/// onto each job's <see cref="ExecutionJobSpec.Parameters"/>.
/// </para>
/// </summary>
internal sealed class TileCacheBatchOptions
{
    /// <summary>Configuration section that binds these options.</summary>
    public const string SectionName = "TileOperations:Batch";

    /// <summary>
    /// When <c>true</c>, tile operations are submitted as execution jobs and
    /// dispatched to <see cref="Backend"/>. Defaults to <c>false</c> so the
    /// in-process path remains the zero-config default.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Batch compute backend adapter identifier. Defaults to the in-process
    /// <c>local</c> backend so enabling batch dispatch without selecting a cloud
    /// backend still runs through the canonical execution-job machinery (durable
    /// record, queue, reconciler) on the same pod rather than the legacy channel.
    /// </summary>
    public string Backend { get; set; } = "local";

    /// <summary>
    /// Batch backend family used to resolve the adapter. Defaults to
    /// <see cref="BatchComputeTargetKind.KubernetesJob"/>, which is also the family
    /// the local backend advertises.
    /// </summary>
    public BatchComputeTargetKind TargetKind { get; set; } = BatchComputeTargetKind.KubernetesJob;

    /// <summary>
    /// Container image / artifact reference for the worker that runs the job on a
    /// remote backend. Ignored by the local in-process backend.
    /// </summary>
    public string? Artifact { get; set; }

    /// <summary>
    /// Optional specialized runtime profile for the worker (for example a
    /// GDAL/tippecanoe-capable image family). Null leaves the job managed/default.
    /// </summary>
    public string? RuntimeProfile { get; set; }

    /// <summary>
    /// Upper bound on the number of tiles a single batch-dispatched job may
    /// generate. The legacy in-process path is hard-capped at 5,000 tiles to
    /// protect the serving pod; batch jobs run on dedicated compute and are bounded
    /// by backend timeout/resources instead, so this ceiling defaults to one
    /// million and exists only as a runaway guardrail.
    /// </summary>
    public int MaxTiles { get; set; } = 1_000_000;

    /// <summary>
    /// Backend-specific parameters merged onto every tile-cache
    /// <see cref="ExecutionJobSpec.Parameters"/> (for example AWS Batch
    /// <c>batch.job_definition_arn</c> / <c>batch.job_queue_arn</c> or Kubernetes
    /// namespace/image overrides).
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
}
