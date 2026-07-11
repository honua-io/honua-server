// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing.CustomCode.LocalBackend;

/// <summary>
/// Production <see cref="ICustomCodeWorkloadPreparer"/>: materializes the pinned commit with the
/// <c>git</c> CLI into the contained checkout directory and resolves a Python launch spec that runs
/// the entrypoint in isolated mode.
/// </summary>
/// <remarks>
/// <para>
/// The git fetch is the backend's OWN, controlled network operation (the repo was allowlisted and the
/// commit SHA-pinned at submit time). It runs in the honua-server process — NOT inside the sandbox —
/// so the untrusted subprocess still has no network by design. Git is invoked with a cleared, minimal
/// environment and <c>GIT_TERMINAL_PROMPT=0</c> so it can never hang on a credential prompt or inherit
/// host secrets, and every git step is bounded by a timeout.
/// </para>
/// <para>
/// The checkout pins to the exact SHA (<c>git fetch --depth 1 &lt;sha&gt;</c> then
/// <c>checkout --detach</c>) and verifies <c>HEAD</c> equals the requested SHA, so a compromised or
/// racing remote cannot swap in different code. Dependency installation is intentionally NOT performed
/// (the sandboxed subprocess has no network): a python job on this MVP backend must be dependency-free
/// or run against a pre-provisioned interpreter environment. This is documented in the security model.
/// </para>
/// </remarks>
internal sealed partial class GitPythonWorkloadPreparer : ICustomCodeWorkloadPreparer
{
    private static readonly TimeSpan GitStepTimeout = TimeSpan.FromMinutes(2);

    private readonly IOptionsMonitor<CustomCodeLocalBackendOptions> _options;
    private readonly ILogger<GitPythonWorkloadPreparer> _logger;

    public GitPythonWorkloadPreparer(
        IOptionsMonitor<CustomCodeLocalBackendOptions> options,
        ILogger<GitPythonWorkloadPreparer> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<SandboxLaunchSpec> PrepareAsync(
        CustomCodeJobInputs inputs,
        string checkoutDirectory,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        Directory.CreateDirectory(checkoutDirectory);

        // Pin to the exact commit: init an empty repo, fetch only that SHA at depth 1, and detach onto
        // it. Fetching the SHA directly (rather than a branch) means a moved branch cannot redirect us.
        await RunGitAsync(options, checkoutDirectory, cancellationToken, "init", "-q").ConfigureAwait(false);
        await RunGitAsync(options, checkoutDirectory, cancellationToken, "remote", "add", "origin", inputs.RepoUrl).ConfigureAwait(false);
        await RunGitAsync(options, checkoutDirectory, cancellationToken, "fetch", "--depth", "1", "--no-tags", "origin", inputs.CommitSha).ConfigureAwait(false);
        await RunGitAsync(options, checkoutDirectory, cancellationToken, "checkout", "--detach", "FETCH_HEAD").ConfigureAwait(false);

        // Defense in depth: confirm the working tree is exactly the pinned commit.
        var head = (await RunGitAsync(options, checkoutDirectory, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
        if (!string.Equals(head, inputs.CommitSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new CustomCodeWorkloadPreparationException(
                $"Checked-out commit '{head}' does not match the pinned SHA; refusing to run.");
        }

        // The deps manifest was validated relative at submit time; confirm it resolves inside the
        // checkout (never an absolute path or a '..' escape) before we hand the checkout to the runtime.
        var manifestPath = SandboxPaths.ResolveContained(checkoutDirectory, inputs.DepsManifest);
        if (!File.Exists(manifestPath))
        {
            Log.ManifestMissing(_logger, inputs.DepsManifest);
        }

        return BuildLaunchSpec(inputs, checkoutDirectory, options);
    }

    private static SandboxLaunchSpec BuildLaunchSpec(
        CustomCodeJobInputs inputs,
        string checkoutDirectory,
        CustomCodeLocalBackendOptions options)
    {
        if (string.Equals(inputs.Runtime, CustomCodeJobContract.PythonRuntime, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(options.PythonExecutable))
            {
                throw new CustomCodeWorkloadPreparationException(
                    "The local custom-code backend requires Geoprocessing:CustomCode:Local:PythonExecutable to run a python job.");
            }

            // -I: isolated mode (ignore PYTHON* env, user site-packages, and the script's own dir on
            //     sys.path) so only what we place there is importable.
            // -B: do not write .pyc files into the checkout.
            // The bootstrap imports the entrypoint module from the checkout (passed as argv, not
            // interpolated) and calls its function with CUSTOMCODE_PARAMS_JSON.
            return new SandboxLaunchSpec(
                options.PythonExecutable!,
                ["-I", "-B", "-c", PythonBootstrap, checkoutDirectory, inputs.Entrypoint]);
        }

        // .NET custom code (Phase 2) needs a build step the sandboxed, network-denied subprocess cannot
        // perform on this MVP backend. Fail closed rather than pretend to support it.
        throw new CustomCodeWorkloadPreparationException(
            $"The local custom-code backend MVP supports only the '{CustomCodeJobContract.PythonRuntime}' runtime (got '{inputs.Runtime}').");
    }

    /// <summary>
    /// The python entrypoint bootstrap. Reads the checkout dir and entrypoint from argv (so nothing is
    /// interpolated into the source), imports the module, and invokes the function with the opaque user
    /// params. Kept minimal and dependency-free.
    /// </summary>
    private const string PythonBootstrap =
        "import sys, os, json, importlib\n" +
        "checkout, entry = sys.argv[1], sys.argv[2]\n" +
        "sys.path.insert(0, checkout)\n" +
        "module_name, _, func_name = entry.partition(':')\n" +
        "func = getattr(importlib.import_module(module_name), func_name)\n" +
        "params = json.loads(os.environ.get('CUSTOMCODE_PARAMS_JSON') or '{}')\n" +
        "func(params)\n";

    private static async Task<string> RunGitAsync(
        CustomCodeLocalBackendOptions options,
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(options.GitExecutable) ? "git" : options.GitExecutable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Minimal, cleared environment: git never inherits honua-server host secrets, and cannot hang
        // on an interactive credential/askpass prompt.
        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] = options.Path;
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["HOME"] = workingDirectory;

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) { stdout.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) { stderr.AppendLine(e.Data); } } };

        try
        {
            if (!process.Start())
            {
                throw new CustomCodeWorkloadPreparationException("Failed to start the git process for the custom-code checkout.");
            }
        }
        catch (Exception ex) when (ex is not CustomCodeWorkloadPreparationException)
        {
            throw new CustomCodeWorkloadPreparationException($"Could not launch git ('{startInfo.FileName}'): {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(GitStepTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new CustomCodeWorkloadPreparationException(
                $"git {arguments.FirstOrDefault()} timed out after {GitStepTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s during the custom-code checkout.");
        }

        if (process.ExitCode != 0)
        {
            string detail;
            lock (stderr)
            {
                detail = stderr.ToString().Trim();
            }

            throw new CustomCodeWorkloadPreparationException(
                $"git {arguments.FirstOrDefault()} failed (exit {process.ExitCode.ToString(CultureInfo.InvariantCulture)}) during the custom-code checkout: {Truncate(detail)}");
        }

        lock (stdout)
        {
            return stdout.ToString();
        }
    }

    private static string Truncate(string value)
        => value.Length <= 300 ? value : value[..300];

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already exited or cannot be killed; nothing more to do.
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9330, LogLevel.Warning,
            "Custom-code deps manifest '{Manifest}' was not found in the checkout; continuing without dependency provisioning (the local backend does not install dependencies)")]
        public static partial void ManifestMissing(ILogger logger, string manifest);
    }
}
