// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;

namespace Honua.Geoprocessing.CustomCode.LocalBackend;

/// <summary>
/// Configuration for the opt-in <c>LocalProcessCustomCodeBackend</c> — the no-cloud-infra path that
/// runs an operator-allowlisted, git-pinned custom-code job as an OS-sandboxed subprocess on the
/// honua-server host itself. Bound from <c>Geoprocessing:CustomCode:Local</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Security model (read before enabling).</b> This backend executes untrusted user code on the
/// server host. Its isolation is OS-process-level and is deliberately layered:
/// </para>
/// <list type="bullet">
///   <item><description><b>Environment allowlist</b> — the subprocess inherits <em>nothing</em>
///   from the honua-server process environment. Only the custom-code contract variables
///   (<c>CUSTOMCODE_*</c> / job-scoped <c>HONUA_*</c>), a controlled minimal <see cref="Path"/>, and
///   the names explicitly listed in <see cref="EnvironmentAllowlist"/> are exposed. Host credentials
///   (cloud keys, DB strings) in the parent environment are never leaked.</description></item>
///   <item><description><b>Wall-clock timeout</b> — every job is hard-killed (whole process tree)
///   at <see cref="MaxWallClock"/>, so a spun-forever script cannot occupy a slot indefinitely.</description></item>
///   <item><description><b>CPU + address-space limits</b> — on POSIX hosts the child is launched
///   through a <c>ulimit</c> wrapper (<c>RLIMIT_CPU</c> / <c>RLIMIT_AS</c>) enforced by the kernel.
///   On non-POSIX hosts these are NOT enforced in-process (documented; run inside a
///   cgroup-constrained container there).</description></item>
///   <item><description><b>Scoped single-use scratch directory</b> — a fresh directory is created
///   per job under <see cref="WorkingRoot"/>, is the subprocess working directory and the only path
///   handed to it, and is deleted when the job reaches a terminal state.</description></item>
///   <item><description><b>Network</b> — NOT denied by this backend. OS-process-level network
///   isolation is not portably enforceable without a namespace/container boundary this MVP does not
///   own. Treat network denial as a deployment requirement: run this backend inside an
///   already-network-restricted container/namespace. <see cref="AllowNetwork"/> is advisory only and
///   defaults to <see langword="false"/> to record the intended posture.</description></item>
/// </list>
/// </remarks>
public sealed class CustomCodeLocalBackendOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Geoprocessing:CustomCode:Local";

    /// <summary>
    /// Master switch for the local backend. Defaults to <see langword="false"/>: even when
    /// <c>Geoprocessing:CustomCode:Backend=Local</c> selects this backend, it fails closed unless the
    /// operator explicitly sets this to <see langword="true"/>. This is the belt-and-suspenders
    /// confirmation that a host operator has accepted the OS-process-level-only isolation model.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Root directory under which each job's single-use scratch/checkout directory is created and
    /// then deleted on terminal. When unset, a <c>honua-customcode-local</c> folder under the system
    /// temp directory is used. Must be an absolute path the honua-server process can write to and
    /// that is not shared with any sensitive data.
    /// </summary>
    public string? WorkingRoot { get; set; }

    /// <summary>
    /// Maximum number of concurrent custom-code subprocesses. Bounds host resource pressure on a
    /// single node. Defaults to 1 (conservative — this surface runs untrusted code).
    /// </summary>
    public int MaxConcurrentProcesses { get; set; } = 1;

    /// <summary>
    /// Hard wall-clock ceiling for a single job. The process tree is killed when it is exceeded.
    /// Defaults to 5 minutes. Independent of (and typically shorter than)
    /// <see cref="CustomCodeOptions.JobTimeout"/>, which bounds the scoped-token lifetime.
    /// </summary>
    public TimeSpan MaxWallClock { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// CPU-time ceiling (RLIMIT_CPU) applied on POSIX hosts via <c>ulimit -t</c>. A job that burns
    /// more CPU-seconds than this is terminated by the kernel. Defaults to 60 seconds. Set to
    /// <see langword="null"/> to not apply a CPU limit (not recommended).
    /// </summary>
    public TimeSpan? MaxCpuTime { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Address-space ceiling in bytes (RLIMIT_AS) applied on POSIX hosts via <c>ulimit -v</c>. A job
    /// whose virtual memory exceeds this fails its next allocation. Defaults to 512 MiB. Set to
    /// <see langword="null"/> to not apply a memory limit (not recommended).
    /// </summary>
    public long? MaxAddressSpaceBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>
    /// Advisory-only network posture. Defaults to <see langword="false"/> to record that the
    /// subprocess is intended to have no outbound network. This backend does NOT enforce network
    /// denial at the OS level (see the security model on the type); enforce it with a
    /// network-restricted container/namespace around honua-server. Setting this to
    /// <see langword="true"/> only documents that a deployment has intentionally allowed network.
    /// </summary>
    public bool AllowNetwork { get; set; }

    /// <summary>
    /// Host environment-variable names the subprocess is permitted to inherit from the honua-server
    /// process, in addition to the custom-code contract variables. Empty by default: the subprocess
    /// inherits none of the host environment. Never add secret-bearing names here.
    /// </summary>
    public List<string> EnvironmentAllowlist { get; set; } = [];

    /// <summary>
    /// The controlled <c>PATH</c> value exposed to the subprocess (the host <c>PATH</c> is never
    /// inherited). Defaults to <c>/usr/bin:/bin</c>. Set to the minimal set of directories the
    /// configured interpreter needs.
    /// </summary>
    public string Path { get; set; } = "/usr/bin:/bin";

    /// <summary>
    /// Absolute path to the Python interpreter used to run <c>customcode.runtime=python</c> jobs
    /// (isolated mode, <c>-I</c>). Required to run python jobs on this backend; when unset a python
    /// job fails closed with a clear message rather than guessing an interpreter.
    /// </summary>
    public string? PythonExecutable { get; set; }

    /// <summary>
    /// Absolute path to the <c>git</c> executable used to materialize the pinned commit. Defaults to
    /// <c>git</c> (resolved via <see cref="Path"/> at checkout time, in the honua-server process, not
    /// the sandbox). The checkout is the backend's own controlled network operation; the subprocess
    /// running user code has no network by design.
    /// </summary>
    public string GitExecutable { get; set; } = "git";

    /// <summary>
    /// Registered local custom tools surfaced through the process catalog so ArcGIS clients can
    /// discover them via the GPServer task-listing / task-info routes. Empty by default (nothing new
    /// is advertised). Each entry is metadata only — it does not by itself grant execution, which is
    /// still gated by the repo allowlist and this backend's controls.
    /// </summary>
    public List<CustomCodeLocalToolDefinition> Tools { get; set; } = [];
}

/// <summary>
/// Discovery metadata for a registered local custom tool: enough for the GPServer task-listing and
/// task-info routes to advertise the tool and its input parameter schema to an ArcGIS client.
/// </summary>
public sealed class CustomCodeLocalToolDefinition
{
    /// <summary>
    /// Stable dotted process id the tool is addressed by (e.g. <c>customcode.my-tool</c>). Surfaced
    /// as the GPServer task name.
    /// </summary>
    public string ProcessId { get; set; } = string.Empty;

    /// <summary>Short human-readable title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>One-sentence description of what the tool does.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Declared input parameters for the tool's schema.</summary>
    public List<CustomCodeLocalToolParameter> Parameters { get; set; } = [];
}

/// <summary>A single declared input parameter for a <see cref="CustomCodeLocalToolDefinition"/>.</summary>
public sealed class CustomCodeLocalToolParameter
{
    /// <summary>Machine-readable parameter name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable label.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Short description of what the parameter controls.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Whether the parameter must be supplied.</summary>
    public bool Required { get; set; }
}

/// <summary>Validates <see cref="CustomCodeLocalBackendOptions"/> at startup.</summary>
internal sealed class CustomCodeLocalBackendOptionsValidator : OptionsValidator<CustomCodeLocalBackendOptions>
{
    protected override void ValidateOptions(CustomCodeLocalBackendOptions options, List<string> failures)
    {
        if (options.MaxConcurrentProcesses <= 0)
        {
            failures.Add($"{CustomCodeLocalBackendOptions.SectionName}:MaxConcurrentProcesses must be greater than 0.");
        }

        if (options.MaxWallClock <= TimeSpan.Zero || options.MaxWallClock > TimeSpan.FromHours(24))
        {
            failures.Add($"{CustomCodeLocalBackendOptions.SectionName}:MaxWallClock must be between 1 second and 24 hours.");
        }

        if (options.MaxCpuTime is { } cpu && cpu <= TimeSpan.Zero)
        {
            failures.Add($"{CustomCodeLocalBackendOptions.SectionName}:MaxCpuTime must be positive when specified.");
        }

        if (options.MaxAddressSpaceBytes is { } mem && mem <= 0)
        {
            failures.Add($"{CustomCodeLocalBackendOptions.SectionName}:MaxAddressSpaceBytes must be positive when specified.");
        }

        if (options.WorkingRoot is { Length: > 0 } root && !System.IO.Path.IsPathFullyQualified(root))
        {
            failures.Add($"{CustomCodeLocalBackendOptions.SectionName}:WorkingRoot must be an absolute path when specified.");
        }

        foreach (var tool in options.Tools)
        {
            if (string.IsNullOrWhiteSpace(tool.ProcessId))
            {
                failures.Add($"{CustomCodeLocalBackendOptions.SectionName}:Tools entries must have a non-empty ProcessId.");
            }
        }
    }
}
