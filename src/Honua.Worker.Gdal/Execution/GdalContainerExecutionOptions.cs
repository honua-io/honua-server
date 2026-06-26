// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Configuration for the opt-in container-exec GDAL command runner
/// (<see cref="DockerGdalCommandRunner"/>, issue #2180). When the GP Devkit local
/// runner runs in <c>--real-worker</c> mode, native-profile (<c>gdal.*</c>) steps
/// are executed by shelling each GDAL/OGR tool out into a fresh
/// <c>docker run</c> of the real <c>honua-worker-etl</c> image — the SAME image
/// Batch packages — instead of the host's in-process GDAL CLIs. This exercises the
/// image / CRS data / driver set / arg handling at the container boundary a
/// production native submit would actually cross, closing the fidelity cliff
/// where an op that passes <c>gp run</c> locally could still fail at the image
/// boundary it never touched.
/// </summary>
internal sealed class GdalContainerExecutionOptions
{
    /// <summary>
    /// Default container image reference for the heavyweight GDAL/ETL worker. Matches
    /// the local build tag produced by <c>docker/worker-gdal/Dockerfile</c>
    /// (<c>docker build -t honua-worker-etl .</c>).
    /// </summary>
    public const string DefaultImage = "honua-worker-etl";

    /// <summary>
    /// The container image to run each GDAL tool inside. Defaults to the local
    /// <see cref="DefaultImage"/> tag; override to pin a published/digest-pinned ref.
    /// </summary>
    public string Image { get; set; } = DefaultImage;

    /// <summary>
    /// The container runtime CLI used to launch the image. Defaults to <c>docker</c>;
    /// override to <c>podman</c> or an absolute path on hosts where it is not on PATH.
    /// </summary>
    public string DockerExecutable { get; set; } = "docker";

    /// <summary>
    /// The uid:gid the container process runs as. Defaults to <c>1001:1001</c> — the
    /// non-root <c>honua</c> user the worker image declares — so artifacts the GDAL
    /// tool writes into the bind-mounted scratch workspace are owned consistently and
    /// remain readable by the host runner after the container exits.
    /// </summary>
    public string User { get; set; } = "1001:1001";

    /// <summary>
    /// The container network mode. Defaults to <c>none</c>: every GP Devkit native op
    /// operates purely on the bind-mounted scratch workspace (base64 inputs are
    /// materialized to files before the tool runs, outputs are read back after), so no
    /// network is required and disabling it keeps the local dry-run hermetic.
    /// </summary>
    public string Network { get; set; } = "none";
}
