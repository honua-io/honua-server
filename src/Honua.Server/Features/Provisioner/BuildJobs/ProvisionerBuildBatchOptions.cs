// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Provisioner.BuildJobs;

/// <summary>
/// Config-driven backend selection for per-area geocoder/router build execution jobs.
/// Mirrors <c>TileCacheBatchOptions</c>: when <see cref="Enabled"/> is set, the build
/// submission path dispatches work as durable <see cref="ExecutionJobKind.GeocoderBuild"/>
/// / <see cref="ExecutionJobKind.RouterBuild"/> execution jobs to the configured
/// <see cref="Backend"/> (the same <c>IBatchComputeBackend</c> set tiling uses, including
/// <c>honua-aws-batch</c> on Fargate Spot, scale-to-zero). When disabled (the default), the
/// in-process <c>local</c> backend runs the canonical execution-job machinery on the same
/// pod, so the capability is exercisable without any cloud configuration.
/// </summary>
internal sealed class ProvisionerBuildBatchOptions
{
    /// <summary>Configuration section that binds these options.</summary>
    public const string SectionName = "Provisioner:BuildJobs:Batch";

    /// <summary>
    /// When <c>true</c>, build operations are submitted as execution jobs and dispatched to
    /// <see cref="Backend"/>. Defaults to <c>false</c> so the in-process path remains the
    /// zero-config default.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Batch compute backend adapter identifier. Defaults to the in-process <c>local</c>
    /// backend so enabling dispatch without selecting a cloud backend still runs through the
    /// canonical execution-job machinery (durable record, queue, reconciler) on the same pod.
    /// </summary>
    public string Backend { get; set; } = "local";

    /// <summary>
    /// Batch backend family used to resolve the adapter. Defaults to
    /// <see cref="BatchComputeTargetKind.KubernetesJob"/>, which the local backend advertises.
    /// </summary>
    public BatchComputeTargetKind TargetKind { get; set; } = BatchComputeTargetKind.KubernetesJob;

    /// <summary>
    /// Default container image / artifact reference for the worker that runs build jobs on a
    /// remote backend. Ignored by the local in-process backend. Per-kind overrides
    /// (<see cref="GeocoderArtifact"/>/<see cref="RouterArtifact"/>) take precedence so the
    /// GDAL/Nominatim and GDAL/osm2pgrouting images can differ.
    /// </summary>
    public string? Artifact { get; set; }

    /// <summary>Geocoder-build worker image override (e.g. a Nominatim-import-capable image).</summary>
    public string? GeocoderArtifact { get; set; }

    /// <summary>Router-build worker image override (e.g. an osm2pgrouting-capable image).</summary>
    public string? RouterArtifact { get; set; }

    /// <summary>
    /// Optional specialized runtime profile for the worker (e.g. a GDAL-capable image family).
    /// Null leaves the job managed/default.
    /// </summary>
    public string? RuntimeProfile { get; set; }

    /// <summary>
    /// Backend-specific parameters merged onto every build job's
    /// <see cref="ExecutionJobSpec.Parameters"/> (for example AWS Batch
    /// <c>batch.job_definition_arn</c> / <c>batch.job_queue_arn</c> and the target artifact
    /// bucket). Identical mechanism to the tile-cache dispatch contract.
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
}
