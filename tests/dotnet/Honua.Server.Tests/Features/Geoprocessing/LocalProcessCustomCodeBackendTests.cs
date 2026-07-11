// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.CustomCode;
using Honua.Geoprocessing.CustomCode.LocalBackend;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Adversarial coverage for <see cref="LocalProcessCustomCodeBackend"/> — the OS-sandboxed-subprocess
/// custom-code backend. Beyond a happy-path run, each security control is exercised by a test that
/// tries to break it: leaking a host secret through the environment, escaping via <c>/proc</c> in the
/// same-UID mode, exposing the raw job token, spinning forever past the wall-clock, escaping the
/// address-space/CPU/output-size limits, and traversing out of the scratch directory. The
/// subprocess-spawning tests use POSIX <c>/bin/sh</c> (mirroring the sibling
/// <c>LocalProcessPoolBatchComputeBackendTests</c>); they run on the Linux CI host, which is
/// non-root — so tests that require an actual privilege drop assert the fail-closed behavior when the
/// drop is impossible, rather than the (untestable-without-root) success case.
/// </summary>
public sealed class LocalProcessCustomCodeBackendTests
{
    private static bool Posix => !OperatingSystem.IsWindows();

    private static bool SetprivAvailable => Posix &&
        (File.Exists("/usr/bin/setpriv") || File.Exists("/bin/setpriv") || File.Exists("/usr/sbin/setpriv"));

    // ---------------------------------------------------------------------
    // Control 1: environment allowlist — the subprocess inherits no host secret.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task Environment_HostSecretIsNotLeaked_ButAllowlistedAndContractVarsAre()
    {
        if (!Posix) { return; }

        const string secretName = "HONUA_TEST_LOCALCC_SECRET";
        const string allowedName = "HONUA_TEST_LOCALCC_ALLOWED";
        Environment.SetEnvironmentVariable(secretName, "topsecret-should-not-leak");
        Environment.SetEnvironmentVariable(allowedName, "operator-allowed-value");
        try
        {
            var outFile = TempFile();
            // Write: <secret-or-EMPTY>|<allowlisted>|<contract CUSTOMCODE_RUNTIME>
            var script =
                $"printf '%s|%s|%s' \"${{{secretName}:-EMPTY}}\" \"${{{allowedName}:-EMPTY}}\" \"${{CUSTOMCODE_RUNTIME:-EMPTY}}\" > \"{outFile}\"";

            var options = Options(o => o.EnvironmentAllowlist = [allowedName]);
            using var backend = Backend(options, new ShellScriptPreparer(script));
            var job = CustomCodeJob("cc-env");

            (await RunToTerminalAsync(backend, job)).Status.Should().Be(ExecutionJobStatus.Succeeded);

            var parts = (await File.ReadAllTextAsync(outFile)).Split('|');
            parts[0].Should().Be("EMPTY", "the host secret must never be inherited by the sandboxed subprocess");
            parts[1].Should().Be("operator-allowed-value", "an explicitly allowlisted host var is passed through");
            parts[2].Should().Be("python", "the CUSTOMCODE_* contract vars from the spec are exposed");
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, null);
            Environment.SetEnvironmentVariable(allowedName, null);
        }
    }

    // ---------------------------------------------------------------------
    // F1 (adversarial review): UID separation is a code-enforced gate, and without it the env
    // allowlist does NOT stop a same-UID script reading the parent's full environment via /proc.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task Gate_NoSandboxUserAndNotAcknowledged_FailsClosed()
    {
        // Deliberately bypass the Options() helper's default acknowledgement so this exercises the
        // actual default posture: SandboxUser unset, risk not acknowledged.
        var options = new CustomCodeLocalBackendOptions
        {
            Enabled = true,
            SandboxUser = null,
            AcknowledgeUnconfinedExecutionRisk = false,
        };
        using var backend = Backend(options, new ShellScriptPreparer("true"));

        var start = await backend.StartAsync(CustomCodeJob("cc-gate"));

        start.Status.Should().Be(ExecutionJobStatus.Failed);
        start.Message.Should().Contain("SandboxUser");
    }

    [Fact]
    public async Task SameUidFileRead_WithoutSandboxUser_CurrentlySucceeds_DocumentingSameUidRisk()
    {
        // Guarded directly with OperatingSystem.IsWindows() (rather than the Posix property) so the
        // platform-compatibility analyzer recognizes this as a genuine POSIX-only guard around the
        // Unix-only File.SetUnixFileMode call below.
        if (OperatingSystem.IsWindows()) { return; }

        // Demonstrates the same-UID risk with a vector that is deterministic across hosts: a "secret"
        // file mode-600-owned by the CURRENT process's UID — the same UID the sandboxed child runs as
        // in the "acknowledged unconfined" mode — sitting OUTSIDE the job's scratch directory (so
        // scratch confinement is irrelevant here; this is a pure DAC same-UID check).
        //
        // We deliberately do NOT demonstrate this via /proc/<ppid>/environ (reading the PARENT's
        // environment from the CHILD): on hosts running Linux's Yama LSM in restricted mode
        // (kernel.yama.ptrace_scope=1 — confirmed to be this test host's default, and Ubuntu's distro
        // default) that direction is blocked by the KERNEL, because Yama's ancestor rule requires the
        // READER to be an ancestor of the target, not the reverse. That is a real, valuable kernel
        // mitigation this backend does not control or rely on — but asserting on it here would make the
        // test flaky across hosts with different ptrace_scope values. The DAC file-read vector below
        // proves the same underlying same-UID risk (arbitrary file read of anything honua-server's own
        // OS user can read) without depending on ptrace/Yama configuration at all.
        var secretFile = TempFile();
        await File.WriteAllTextAsync(secretFile, "same-uid-file-read-marker");
        File.SetUnixFileMode(secretFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        try
        {
            var outFile = TempFile();
            var script = $"cat \"{secretFile}\" > \"{outFile}\" 2>/dev/null || echo READFAILED > \"{outFile}\"";
            var options = Options(); // AcknowledgeUnconfinedExecutionRisk = true, no SandboxUser
            using var backend = Backend(options, new ShellScriptPreparer(script));

            (await RunToTerminalAsync(backend, CustomCodeJob("cc-file-read"))).Status.Should().Be(ExecutionJobStatus.Succeeded);

            (await File.ReadAllTextAsync(outFile)).Should().Contain(
                "same-uid-file-read-marker",
                "without SandboxUser the sandboxed child runs under the SAME OS user as its parent and " +
                "can read any file that user owns via plain DAC permissions — regardless of scratch-dir " +
                "confinement or the environment allowlist — which is why AcknowledgeUnconfinedExecutionRisk " +
                "exists and why SandboxUser is the recommended posture");
        }
        finally
        {
            File.Delete(secretFile);
        }
    }

    [Fact]
    public async Task SandboxUser_WhenPrivilegeDropIsImpossible_FailsClosed_NeverRunsUnconfined()
    {
        if (!SetprivAvailable) { return; }

        // The test runner is non-root (typical CI), so setpriv cannot actually drop to a different real
        // UID (EPERM). Before the F4 fix this class of failure was invisible; now the wrapper's `exec
        // setpriv ...` failing must abort the whole launch — proving the backend never silently falls
        // back to running the job unconfined when the configured privilege drop cannot happen.
        var outFile = TempFile();
        var options = Options(o =>
        {
            o.SandboxUser = "nobody";
            o.AcknowledgeUnconfinedExecutionRisk = false; // SandboxUser alone must satisfy the gate
        });
        using var backend = Backend(options, new ShellScriptPreparer($"echo SHOULD_NOT_RUN > \"{outFile}\""));

        var terminal = await RunToTerminalAsync(backend, CustomCodeJob("cc-drop-fails"));

        terminal.Status.Should().Be(ExecutionJobStatus.Failed);
        File.Exists(outFile).Should().BeFalse(
            "the target script must never execute when the privilege drop to SandboxUser fails");
    }

    // ---------------------------------------------------------------------
    // F2 (adversarial review): the raw HONUA_JOB_TOKEN callback credential must never reach the child.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task Environment_JobTokenIsNeverExposedToTheChild()
    {
        if (!Posix) { return; }

        var outFile = TempFile();
        var script = $"printf '%s' \"${{HONUA_JOB_TOKEN:-EMPTY}}\" > \"{outFile}\"";
        using var backend = Backend(Options(), new ShellScriptPreparer(script));

        var job = CustomCodeJob("cc-token");
        job = job with
        {
            Spec = job.Spec with
            {
                Parameters = new Dictionary<string, string>(job.Spec.Parameters, StringComparer.Ordinal)
                {
                    [CustomCodeJobContract.JobTokenEnvParam] = "sekrit-scoped-bearer-token",
                    [CustomCodeJobContract.BaseUrlEnvParam] = "https://honua.example/api",
                },
            },
        };

        (await RunToTerminalAsync(backend, job)).Status.Should().Be(ExecutionJobStatus.Succeeded);

        (await File.ReadAllTextAsync(outFile)).Should().Be(
            "EMPTY",
            "HONUA_JOB_TOKEN must never be placed in the sandboxed child's environment, even though the " +
            "job spec carries it for the Batch backend's callback client");
    }

    // ---------------------------------------------------------------------
    // Control 2: wall-clock timeout — a spin-forever job is hard-killed.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task WallClock_SpinForeverJob_IsKilledAndReportedFailed()
    {
        if (!Posix) { return; }

        var options = Options(o =>
        {
            o.MaxWallClock = TimeSpan.FromSeconds(2);
            o.MaxCpuTime = null; // isolate the wall-clock control from the CPU limit
        });
        // Busy-spin (never exits on its own); only the wall-clock kill can end it.
        using var backend = Backend(options, new ShellScriptPreparer("while true; do :; done"));
        var job = CustomCodeJob("cc-spin");

        var sw = Stopwatch.StartNew();
        var terminal = await RunToTerminalAsync(backend, job);
        sw.Stop();

        terminal.Status.Should().Be(ExecutionJobStatus.Failed);
        terminal.Message.Should().Contain("wall-clock");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "the deadline must fire well before the poll timeout");
    }

    // ---------------------------------------------------------------------
    // Control 3: CPU + address-space limits are applied to the child (kernel-enforced).
    // Proven deterministically by reading the limits back inside the sandbox: if the
    // ulimit wrapper were absent the readback would be 'unlimited', not our values.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task ResourceLimits_CpuAndAddressSpace_AreAppliedToTheChild()
    {
        if (!Posix) { return; }

        var outFile = TempFile();
        var options = Options(o =>
        {
            o.MaxCpuTime = TimeSpan.FromSeconds(37);
            o.MaxAddressSpaceBytes = 300L * 1024 * 1024; // 300 MiB => 307200 KiB
        });
        using var backend = Backend(options, new ShellScriptPreparer($"printf '%s|%s' \"$(ulimit -t)\" \"$(ulimit -v)\" > \"{outFile}\""));
        var job = CustomCodeJob("cc-limits");

        (await RunToTerminalAsync(backend, job)).Status.Should().Be(ExecutionJobStatus.Succeeded);

        var parts = (await File.ReadAllTextAsync(outFile)).Split('|');
        parts[0].Should().Be("37", "RLIMIT_CPU (ulimit -t) must be set to the configured CPU-time ceiling");
        parts[1].Should().Be((300L * 1024).ToString(CultureInfo.InvariantCulture), "RLIMIT_AS (ulimit -v, in KiB) must be set to the configured address-space ceiling");
    }

    [Fact]
    public async Task ResourceLimits_MemoryHog_IsContainedByTheAddressSpaceLimit()
    {
        if (!Posix) { return; }

        var options = Options(o =>
        {
            o.MaxAddressSpaceBytes = 64L * 1024 * 1024; // 64 MiB
            o.MaxCpuTime = TimeSpan.FromSeconds(15);     // backstop if the alloc somehow spins
            o.MaxWallClock = TimeSpan.FromSeconds(60);   // long, so the wall-clock never fires first
        });
        // Ask /bin/sh to build an ever-growing string; RLIMIT_AS makes an allocation fail and the
        // shell aborts non-zero long before it can exhaust host memory.
        using var backend = Backend(options, new ShellScriptPreparer("s=x; while true; do s=\"$s$s$s$s$s$s$s$s\"; done"));
        var job = CustomCodeJob("cc-memhog");

        var terminal = await RunToTerminalAsync(backend, job);
        terminal.Status.Should().BeOneOf(ExecutionJobStatus.Failed);
        terminal.Message.Should().NotContain("wall-clock",
            "the address-space limit should abort the allocation before the wall-clock deadline");
    }

    // ---------------------------------------------------------------------
    // F3 (adversarial review): output-file-size ceiling (always) and process-count ceiling (only
    // alongside SandboxUser — RLIMIT_NPROC is per-real-UID host-wide on Linux).
    // ---------------------------------------------------------------------
    [Fact]
    public async Task ResourceLimits_OutputFileSize_IsAppliedRegardlessOfSandboxUser()
    {
        if (!Posix) { return; }

        var outFile = TempFile();
        var options = Options(o => o.MaxOutputFileBytes = 100L * 1024 * 1024); // 100 MiB => 204800 blocks
        using var backend = Backend(options, new ShellScriptPreparer($"printf '%s' \"$(ulimit -f)\" > \"{outFile}\""));

        (await RunToTerminalAsync(backend, CustomCodeJob("cc-fsize"))).Status.Should().Be(ExecutionJobStatus.Succeeded);

        (await File.ReadAllTextAsync(outFile)).Should().Be(
            (100L * 1024 * 1024 / 512).ToString(CultureInfo.InvariantCulture),
            "RLIMIT_FSIZE (ulimit -f, in 512-byte blocks) must be set to the configured output-size ceiling");
    }

    [Fact]
    public async Task ResourceLimits_ProcessCount_IsSkippedWithoutSandboxUser()
    {
        if (!Posix) { return; }

        var outFile = TempFile();
        // MaxProcessCount is configured but SandboxUser is NOT — applying RLIMIT_NPROC here would count
        // against honua-server's own (large) thread/process footprint under the shared UID, so the
        // launch script must skip -u entirely in this mode.
        var options = Options(o => o.MaxProcessCount = 7);
        using var backend = Backend(options, new ShellScriptPreparer($"printf '%s' \"$(ulimit -u)\" > \"{outFile}\""));

        (await RunToTerminalAsync(backend, CustomCodeJob("cc-nproc-skip"))).Status.Should().Be(ExecutionJobStatus.Succeeded);

        (await File.ReadAllTextAsync(outFile)).Should().NotBe("7",
            "ulimit -u must NOT be applied when SandboxUser is unset, regardless of MaxProcessCount");
    }

    // ---------------------------------------------------------------------
    // F4 (adversarial review): a ulimit the wrapper fails to apply must abort the launch (fail closed),
    // not silently continue unconfined. White-box: inspect the generated wrapper script itself so the
    // guarantee is verified independent of any particular host's rlimit ceilings.
    // ---------------------------------------------------------------------
    [Fact]
    public void LaunchScript_UlimitCalls_AbortOnFailure_NotSwallowed()
    {
        if (!Posix) { return; }

        var options = Options(o =>
        {
            o.MaxCpuTime = TimeSpan.FromSeconds(10);
            o.MaxAddressSpaceBytes = 256L * 1024 * 1024;
            o.MaxOutputFileBytes = 50L * 1024 * 1024;
        });
        var spec = new SandboxLaunchSpec("/bin/true", []);
        using var process = SandboxedProcess.Create(spec, Path.GetTempPath(), new Dictionary<string, string>(), options);

        process.StartInfo.FileName.Should().Be("/bin/sh");
        var script = process.StartInfo.ArgumentList[1];

        script.Should().NotContain("2>/dev/null",
            "the old swallow-and-continue pattern must be gone: a ulimit failure has to be visible and fatal");
        script.Should().Contain("ulimit -t 10 || exit 97");
        script.Should().Contain("ulimit -v 262144 || exit 97");
        script.Should().Contain("ulimit -f 102400 || exit 97");
        script.Should().MatchRegex(@"ulimit[^;]*\|\|\s*exit\s+\d+", "every ulimit call must be guarded by a fail-closed '|| exit'");
    }

    // ---------------------------------------------------------------------
    // Control 4: scratch confinement + path-traversal safety.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task Scratch_ChildWorkingDirectory_IsTheSingleUseScratchDir_AndIsDeletedAfter()
    {
        if (!Posix) { return; }

        var root = Path.Combine(Path.GetTempPath(), $"honua-cc-root-{Guid.NewGuid():N}");
        var outFile = TempFile();
        var options = Options(o => o.WorkingRoot = root);
        using var backend = Backend(options, new ShellScriptPreparer($"pwd > \"{outFile}\""));
        var job = CustomCodeJob("cc-cwd");

        (await RunToTerminalAsync(backend, job)).Status.Should().Be(ExecutionJobStatus.Succeeded);

        var cwd = (await File.ReadAllTextAsync(outFile)).Trim();
        SandboxPaths.IsUnder(root, cwd).Should().BeTrue("the subprocess must be rooted inside the scratch directory");

        // After a terminal observe the scratch dir must be gone (single-use, deleted after).
        Directory.Exists(cwd).Should().BeFalse("the single-use scratch directory must be deleted once the job is terminal");

        if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SandboxPaths_RejectsTraversalAndAbsoluteEscapes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"honua-cc-contain-{Guid.NewGuid():N}");

        // A contained relative segment resolves fine.
        SandboxPaths.ResolveContained(root, "checkout/sub").Should().StartWith(Path.GetFullPath(root));

        // '..' traversal escapes and is rejected.
        var traverse = () => SandboxPaths.ResolveContained(root, "../../etc/passwd");
        traverse.Should().Throw<CustomCodePathEscapeException>();

        // An absolute path escapes (Path.Combine discards the root) and is rejected.
        var absolute = () => SandboxPaths.ResolveContained(root, OperatingSystem.IsWindows() ? @"C:\Windows\win.ini" : "/etc/passwd");
        absolute.Should().Throw<CustomCodePathEscapeException>();

        // Operation-id sanitization can never yield a separator or traversal token.
        var seg = SandboxPaths.SanitizeSegment("../../evil/../id");
        seg.Should().NotContain("/").And.NotContain("\\").And.NotContain("..");
    }

    [Fact]
    public async Task Inputs_DepsManifestWithTraversal_IsRejectedClosed()
    {
        if (!Posix) { return; }

        using var backend = Backend(Options(), new ShellScriptPreparer("true"));
        var job = CustomCodeJob("cc-badmanifest", depsManifest: "../../etc/passwd");

        var start = await backend.StartAsync(job);
        start.Status.Should().Be(ExecutionJobStatus.Failed);
        start.Message.Should().Contain("deps_manifest");
    }

    // ---------------------------------------------------------------------
    // Fail-closed gates.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task Disabled_Backend_FailsClosed()
    {
        using var backend = Backend(Options(o => o.Enabled = false), new ShellScriptPreparer("true"));
        var start = await backend.StartAsync(CustomCodeJob("cc-disabled"));
        start.Status.Should().Be(ExecutionJobStatus.Failed);
        start.Message.Should().Contain("not enabled");
    }

    [Fact]
    public void BackendIdentity_MatchesContract()
    {
        using var backend = Backend(Options(), new ShellScriptPreparer("true"));
        backend.BackendName.Should().Be("honua-local-customcode");
        backend.TargetKind.Should().Be(BatchComputeTargetKind.LocalProcess);
    }

    // ---------------------------------------------------------------------
    // F1: options-validator gates, so a misconfigured deployment fails at startup, not at first job.
    // ---------------------------------------------------------------------
    [Fact]
    public void Validator_EnabledWithoutSandboxUserOrAcknowledgement_Fails()
    {
        var result = new CustomCodeLocalBackendOptionsValidator().Validate(
            null, new CustomCodeLocalBackendOptions { Enabled = true });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("SandboxUser");
        result.FailureMessage.Should().Contain("AcknowledgeUnconfinedExecutionRisk");
    }

    [Fact]
    public void Validator_EnabledWithAcknowledgement_Passes()
    {
        var result = new CustomCodeLocalBackendOptionsValidator().Validate(
            null, new CustomCodeLocalBackendOptions { Enabled = true, AcknowledgeUnconfinedExecutionRisk = true });

        result.Failed.Should().BeFalse();
    }

    [Fact]
    public void Validator_EnabledWithValidSandboxUser_Passes()
    {
        var result = new CustomCodeLocalBackendOptionsValidator().Validate(
            null, new CustomCodeLocalBackendOptions { Enabled = true, SandboxUser = "honua-customcode" });

        result.Failed.Should().BeFalse();
    }

    [Fact]
    public void Validator_SandboxUserRoot_Fails()
    {
        var result = new CustomCodeLocalBackendOptionsValidator().Validate(
            null, new CustomCodeLocalBackendOptions { Enabled = true, SandboxUser = "root" });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("root");
    }

    [Fact]
    public void Validator_SandboxUserWithShellMetacharacters_Fails()
    {
        var result = new CustomCodeLocalBackendOptionsValidator().Validate(
            null, new CustomCodeLocalBackendOptions { Enabled = true, SandboxUser = "nobody; rm -rf /" });

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void SandboxedProcess_RejectsUnvalidatedSandboxUserCharset_EvenIfValidatorWasBypassed()
    {
        if (!Posix) { return; }

        // Defense in depth: a hand-built options object (bypassing the registered validator, exactly as
        // a test double or a future caller could do) must still never let a malicious SandboxUser value
        // reach shell interpolation in the launch wrapper.
        var options = Options(o => o.SandboxUser = "nobody`touch /tmp/pwned`");
        var spec = new SandboxLaunchSpec("/bin/true", []);

        var act = () => SandboxedProcess.Create(spec, Path.GetTempPath(), new Dictionary<string, string>(), options);

        act.Should().Throw<InvalidOperationException>();
    }

    // ---------------------------------------------------------------------
    // Discovery (req 4): a configured local tool is surfaced through the process catalog.
    // ---------------------------------------------------------------------
    [Fact]
    public void ToolCatalog_SurfacesConfiguredLocalToolWithParameterSchema()
    {
        var options = new CustomCodeLocalBackendOptions
        {
            Tools =
            [
                new CustomCodeLocalToolDefinition
                {
                    ProcessId = "customcode.my-tool",
                    Title = "My Tool",
                    Description = "A registered local custom tool.",
                    Parameters = [new CustomCodeLocalToolParameter { Name = "threshold", DisplayName = "Threshold", Required = true }],
                }
            ]
        };

        var catalog = new CustomCodeLocalToolCatalog(
            new BuiltInProcessCatalog(NullLogger<BuiltInProcessCatalog>.Instance),
            Microsoft.Extensions.Options.Options.Create(options));

        var tool = catalog.GetProcess("customcode.my-tool");
        tool.Should().NotBeNull();
        tool!.Title.Should().Be("My Tool");
        tool.RuntimeProfile.Should().Be(CustomCodeJobContract.RuntimeProfile);
        tool.Parameters.Should().ContainSingle(p => p.Name == "threshold" && p.Required);

        catalog.ListProcesses().Should().Contain(p => p.ProcessId == "customcode.my-tool");
        // Built-in processes are still present (decoration, not replacement).
        catalog.GetProcess("geometry.buffer").Should().NotBeNull();
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"honua-cc-{Guid.NewGuid():N}.txt");

    /// <summary>
    /// Default test options run in the "acknowledged unconfined" mode (no SandboxUser — CI is non-root
    /// and cannot actually drop privileges) so most of these tests can exercise the OTHER controls
    /// (env allowlist, wall-clock, resource limits, scratch confinement) in isolation. The F1 tests
    /// above explicitly build options WITHOUT the acknowledgement, or WITH SandboxUser, to exercise the
    /// UID-separation gate itself.
    /// </summary>
    private static CustomCodeLocalBackendOptions Options(Action<CustomCodeLocalBackendOptions>? configure = null)
    {
        var options = new CustomCodeLocalBackendOptions
        {
            Enabled = true,
            AcknowledgeUnconfinedExecutionRisk = true,
            MaxConcurrentProcesses = 2,
            MaxWallClock = TimeSpan.FromSeconds(30),
            MaxCpuTime = TimeSpan.FromSeconds(20),
            MaxAddressSpaceBytes = 512L * 1024 * 1024,
            MaxProcessCount = null, // opt in per-test; skipped anyway without SandboxUser
            MaxOutputFileBytes = null, // opt in per-test so unrelated tests aren't coupled to this limit
        };
        configure?.Invoke(options);
        return options;
    }

    private static LocalProcessCustomCodeBackend Backend(
        CustomCodeLocalBackendOptions options,
        ICustomCodeWorkloadPreparer preparer)
        => new(
            new StaticOptionsMonitor<CustomCodeLocalBackendOptions>(options),
            preparer,
            NullLogger<LocalProcessCustomCodeBackend>.Instance);

    private static ExecutionJobRecord CustomCodeJob(string operationId, string depsManifest = "requirements.txt")
    {
        var now = DateTimeOffset.UtcNow;
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CustomCodeJobContract.RuntimeParam] = CustomCodeJobContract.PythonRuntime,
            [CustomCodeJobContract.RepoUrlParam] = "https://github.com/example/tool",
            [CustomCodeJobContract.GitRefParam] = new string('a', 40),
            [CustomCodeJobContract.EntrypointParam] = "tool.main:run",
            [CustomCodeJobContract.DepsManifestParam] = depsManifest,
            // The submit coordinator projects customcode.* onto env.CUSTOMCODE_* — mirror that here so
            // the backend surfaces the contract var CUSTOMCODE_RUNTIME to the child.
            [CustomCodeJobContract.ToEnvParamKey(CustomCodeJobContract.RuntimeEnvName)] = CustomCodeJobContract.PythonRuntime,
        };

        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.LocalProcess,
                Backend = "honua-local-customcode",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "customcode",
                RuntimeProfile = CustomCodeJobContract.RuntimeProfile,
                Parameters = parameters,
            },
        };
    }

    private static async Task<BatchComputeObservation> RunToTerminalAsync(
        LocalProcessCustomCodeBackend backend,
        ExecutionJobRecord job)
    {
        var start = await backend.StartAsync(job);
        if (start.Status is ExecutionJobStatus.Failed)
        {
            return new BatchComputeObservation { Status = start.Status, Message = start.Message };
        }

        var running = job with { Status = ExecutionJobStatus.Running, ProviderOperationId = start.ProviderOperationId };
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            var observation = await backend.ObserveAsync(running);
            if (observation.Status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled)
            {
                return observation;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The custom-code job did not reach a terminal state within 30 seconds.");
    }

    // (assertion helper removed; tests await + assert Status directly)

    /// <summary>
    /// Test preparer that drops the given POSIX shell script into the checkout directory and returns a
    /// launch spec running it under <c>/bin/sh</c> — so the backend's OS-sandbox controls are exercised
    /// without a git remote or a python runtime.
    /// </summary>
    private sealed class ShellScriptPreparer(string script) : ICustomCodeWorkloadPreparer
    {
        public ValueTask<SandboxLaunchSpec> PrepareAsync(
            CustomCodeJobInputs inputs,
            string checkoutDirectory,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(checkoutDirectory);
            var scriptPath = Path.Combine(checkoutDirectory, "entry.sh");
            File.WriteAllText(scriptPath, "#!/bin/sh\n" + script + "\n");
            return ValueTask.FromResult(new SandboxLaunchSpec("/bin/sh", [scriptPath]));
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
