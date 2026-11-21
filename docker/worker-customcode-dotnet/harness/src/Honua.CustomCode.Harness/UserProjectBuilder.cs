// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;

namespace Honua.CustomCode.Harness;

/// <summary>Raised when restoring/building the user's project fails.</summary>
public sealed class UserBuildException(string message) : Exception(message);

/// <summary>The result of building a user project.</summary>
/// <param name="ProjectPath">The resolved absolute path of the user's <c>.csproj</c>.</param>
/// <param name="OutputDirectory">The directory holding the built assemblies.</param>
public sealed record UserBuildResult(string ProjectPath, string OutputDirectory);

/// <summary>
/// Restores + builds the user's <c>.csproj</c> against the warm NuGet cache baked into
/// the image (NetTopologySuite + the Honua .NET SDK), so the common tool needs no
/// network restore. This is the .NET analogue of the Python harness's
/// <c>deps.py</c> (which <c>pip install -r requirements.txt</c>) — for .NET the
/// "deps manifest" IS the user project, and the runtime compile produces the assembly
/// the entrypoint loader activates.
/// </summary>
public sealed class UserProjectBuilder
{
    private readonly Func<IReadOnlyList<string>, string?, ProcessResult> _runner;

    // The image's warm NuGet package folder, the user build's only package source.
    internal static string WarmPackageSource { get; } =
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured ? configured : "/opt/nuget/packages";

    internal static string OfflineNuGetConfigPath { get; } =
        Path.Join(Path.GetDirectoryName(WarmPackageSource) ?? "/opt/nuget", "NuGet.config");

    /// <summary>Creates a project builder.</summary>
    /// <param name="dotnetRunner">An injectable <c>dotnet</c> runner (for tests); defaults to invoking the real SDK.</param>
    public UserProjectBuilder(Func<IReadOnlyList<string>, string?, ProcessResult>? dotnetRunner = null)
    {
        _runner = dotnetRunner ?? RunDotnet;
    }

    /// <summary>
    /// Resolve <paramref name="depsManifest"/> relative to <paramref name="sourceRoot"/>
    /// (no <c>..</c> escapes), then <c>dotnet build -c Release</c> it into
    /// <paramref name="outputDirectory"/>. Returns the built output directory.
    /// </summary>
    /// <param name="sourceRoot">The cloned source root.</param>
    /// <param name="depsManifest">The repo-relative path to the user's <c>.csproj</c>.</param>
    /// <param name="outputDirectory">The directory to emit the build into.</param>
    /// <returns>The resolved project path + the build output directory.</returns>
    /// <exception cref="UserBuildException">On a path-escape, a missing project, or a failed build.</exception>
    public UserBuildResult Build(string sourceRoot, string depsManifest, string outputDirectory)
    {
        // Reject a rooted/absolute manifest path up front: Path.Combine silently drops
        // the source root when the second argument is already absolute, which would
        // otherwise let a job-supplied deps_manifest point anywhere on disk.
        if (Path.IsPathRooted(depsManifest))
        {
            throw new UserBuildException($"deps_manifest '{depsManifest}' must be relative to the source root.");
        }

        var root = Path.GetFullPath(sourceRoot);
        // False positive: the IsPathRooted check immediately above rejects any
        // absolute depsManifest before it reaches this combine.
        var project = Path.GetFullPath(Path.Join(root, depsManifest));

        // Defense-in-depth: the project must stay within the cloned source root.
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!project.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            throw new UserBuildException($"deps_manifest '{depsManifest}' escapes the source root.");
        }

        if (!File.Exists(project))
        {
            throw new UserBuildException($"deps_manifest project not found: {project}");
        }

        Directory.CreateDirectory(outputDirectory);

        // Package restore uses only the warm cache baked into the image: --source overrides any
        // nuget.config in the user's repository, and the empty properties override feeds a user
        // project adds itself. A tool that needs another package gets it from a wider cache, not
        // the network. (Build targets can still run commands, so egress policy remains the
        // network control.)
        var args = new List<string>
        {
            "build",
            project,
            "--configuration", "Release",
            "--nologo",
            "--source", WarmPackageSource,
            "-p:RestoreIgnoreFailedSources=false",
            "-p:RestoreAdditionalProjectSources=",
            "-p:RestoreAdditionalProjectFallbackFolders=",
            "--output", outputDirectory,
            "-p:TreatWarningsAsErrors=false",
            "-p:NuGetAudit=false",
        };
        if (File.Exists(OfflineNuGetConfigPath))
        {
            args.Add($"-p:RestoreConfigFile={OfflineNuGetConfigPath}");
        }

        var result = _runner(args, root);
        if (result.ExitCode != 0)
        {
            var tail = (result.StdErr + "\n" + result.StdOut).Trim();
            if (tail.Length > 4000)
            {
                tail = tail[^4000..];
            }

            throw new UserBuildException($"dotnet build {project} failed ({result.ExitCode}): {tail}");
        }

        return new UserBuildResult(project, outputDirectory);
    }

    private static ProcessResult RunDotnet(IReadOnlyList<string> args, string? cwd)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = cwd ?? string.Empty,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        RemoveCredentials(psi);
        EnsureWritableCliHome(psi, cwd);
        using var process = Process.Start(psi)
            ?? throw new UserBuildException("failed to start dotnet.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    // The build runs the user's MSBuild targets before the harness scrubs its own environment,
    // so the dotnet process starts without the job credentials.
    internal static void RemoveCredentials(ProcessStartInfo psi)
    {
        foreach (var name in CredentialSandbox.StrippedEnvVars)
        {
            psi.Environment.Remove(name);
        }
    }

    internal static void EnsureWritableCliHome(ProcessStartInfo psi, string? fallbackDirectory)
    {
        var fallback = !string.IsNullOrWhiteSpace(fallbackDirectory) && Directory.Exists(fallbackDirectory)
            ? fallbackDirectory
            : Path.GetTempPath();

        EnsureDirectoryEnv(psi, "HOME", fallback);
        EnsureDirectoryEnv(psi, "DOTNET_CLI_HOME", psi.Environment["HOME"]!);
    }

    private static void EnsureDirectoryEnv(ProcessStartInfo psi, string name, string fallback)
    {
        if (psi.Environment.TryGetValue(name, out var existing)
            && !string.IsNullOrWhiteSpace(existing)
            && Directory.Exists(existing))
        {
            return;
        }

        psi.Environment[name] = fallback;
    }
}
