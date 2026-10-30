// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json.Nodes;

namespace Honua.Geoprocessing.Cli;

/// <summary>
/// Dependency-aware discovery for one scaffolded custom-code project (<c>honua-gp.yaml</c>).
/// Registration of a catalog function is a different question: this verdict says whether
/// <em>this</em> project can run on this machine. A missing prerequisite is named and the
/// devkit never turns the project into a server submission.
/// </summary>
public static class GpProjectDiscovery
{
    /// <summary>The language runtime prerequisite for a Python project.</summary>
    public const string PythonRuntimeName = "Python 3 interpreter";

    /// <summary>The language runtime prerequisite for a .NET project.</summary>
    public const string DotnetRuntimeName = ".NET SDK";

    /// <summary>The local harness prerequisite.</summary>
    public const string HarnessName = "local harness";

    /// <summary>Sentence every blocked detail carries so a failure cannot be read as a remote submit.</summary>
    public const string NoSubmit = "The devkit does not submit to a server.";

    /// <summary>
    /// Assesses the project's language runtime, committed dependency lock, and local harness.
    /// </summary>
    /// <param name="manifest">The loaded project descriptor.</param>
    /// <param name="projectDirectory">The project root the lock path is resolved against.</param>
    /// <param name="probe">Machine facts. Tests inject fixed answers.</param>
    /// <param name="resolvedHarnessRoot">A harness directory already found on disk, or null.</param>
    /// <param name="allowInstalledRestore">
    /// Whether a missing harness directory may be satisfied by the matching installed devkit
    /// restoring <c>.honua/toolchain</c> on <c>test</c>. Callers that will not restore pass false.
    /// </param>
    public static GpRunnability Assess(
        GpProjectManifest manifest,
        string projectDirectory,
        GpEnvironmentProbe probe,
        string? resolvedHarnessRoot,
        bool allowInstalledRestore)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(probe);

        var prerequisites = new List<GpPrerequisite>
        {
            Runtime(manifest, probe),
            DependencyLock(manifest, projectDirectory),
            Harness(manifest, resolvedHarnessRoot, allowInstalledRestore),
        };

        return new GpRunnability
        {
            ExecutionMode = GpExecutionMode.Custom,
            Prerequisites = prerequisites,
            SupportedTargets = ["local-harness", "aws-batch"],
        };
    }

    /// <summary>The human-readable description of a project verdict.</summary>
    /// <param name="manifest">The project.</param>
    /// <param name="verdict">The verdict from <see cref="Assess"/>.</param>
    public static string RenderText(GpProjectManifest manifest, GpRunnability verdict)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(verdict);
        var builder = new StringBuilder();
        builder.Append("project     : ").AppendLine(manifest.Name);
        builder.Append("language    : ").AppendLine(manifest.Language == GpProjectLanguage.Python ? "python" : "dotnet");
        builder.Append("entrypoint  : ").AppendLine(manifest.Entrypoint);
        builder.Append("mode        : ").AppendLine(verdict.ExecutionModeLabel);
        builder.Append("runnable    : ").AppendLine(verdict.RunnableHere ? "yes" : $"no — blocked by {verdict.BlockedBy}");
        foreach (var prerequisite in verdict.Prerequisites)
        {
            builder.Append("  needs     : ").Append(prerequisite.Name).Append(" [").Append(prerequisite.Status).Append("] ").AppendLine(prerequisite.Detail);
        }

        builder.Append("targets     : ").AppendLine(string.Join(", ", verdict.SupportedTargets));
        builder.Append(NoSubmit);
        return builder.ToString();
    }

    /// <summary>
    /// The machine-readable project descriptor: identity plus the same runnability fields
    /// <c>list</c> and <c>describe</c> emit for a catalog process.
    /// </summary>
    /// <param name="manifest">The project.</param>
    /// <param name="verdict">The verdict from <see cref="Assess"/>.</param>
    public static JsonObject WriteDescriptor(GpProjectManifest manifest, GpRunnability verdict)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(verdict);
        var descriptor = new JsonObject
        {
            ["id"] = manifest.Name,
            ["language"] = manifest.Language == GpProjectLanguage.Python ? "python" : "dotnet",
            ["entrypoint"] = manifest.Entrypoint,
            ["source"] = "project",
            ["dependenciesVerified"] = true,
        };
        verdict.WriteTo(descriptor);
        return descriptor;
    }

    /// <summary>The first unsatisfied prerequisite, rendered as the <c>test</c> error.</summary>
    /// <param name="manifest">The project.</param>
    /// <param name="verdict">A verdict that is not runnable.</param>
    public static string BlockedError(GpProjectManifest manifest, GpRunnability verdict)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(verdict);
        var blocker = verdict.Prerequisites.First(static p => !p.Satisfied);
        return $"'{manifest.Name}' is not runnable here ({verdict.ExecutionModeLabel}). needs: {blocker.Name} — {blocker.Detail}";
    }

    private static GpPrerequisite Runtime(GpProjectManifest manifest, GpEnvironmentProbe probe)
    {
        if (manifest.Language == GpProjectLanguage.Python)
        {
            return probe.PythonAvailable
                ? new GpPrerequisite(PythonRuntimeName, "satisfied", $"python3 is on PATH. {NoSubmit}")
                : new GpPrerequisite(PythonRuntimeName, "missing", $"python3 is not on PATH. Install Python 3.11 or newer and rerun. {NoSubmit}");
        }

        return probe.ExecutableAvailable("dotnet")
            ? new GpPrerequisite(DotnetRuntimeName, "satisfied", $"dotnet is on PATH. {NoSubmit}")
            : new GpPrerequisite(DotnetRuntimeName, "missing", $"dotnet is not on PATH. Install the .NET 10 SDK and rerun. {NoSubmit}");
    }

    private static GpPrerequisite DependencyLock(GpProjectManifest manifest, string projectDirectory)
    {
        var relative = manifest.DependencyLockPath;
        var name = $"dependency lock ({relative})";
        if (!IsInsideProject(projectDirectory, relative, out var full))
        {
            return new GpPrerequisite(name, "missing", $"'{relative}' must be a relative path inside the project. {NoSubmit}");
        }

        return File.Exists(full)
            ? new GpPrerequisite(name, "satisfied", $"{relative} is present. test restores it with the language toolchain; describe does not import each package. {NoSubmit}")
            : new GpPrerequisite(name, "missing", $"{relative} is not in the project. Commit the lock next to honua-gp.yaml. {NoSubmit}");
    }

    private static GpPrerequisite Harness(GpProjectManifest manifest, string? resolvedHarnessRoot, bool allowInstalledRestore)
    {
        if (resolvedHarnessRoot is not null && IsHarnessRoot(resolvedHarnessRoot))
        {
            return new GpPrerequisite(HarnessName, "satisfied", $"using {resolvedHarnessRoot}. {NoSubmit}");
        }

        var wanted = string.IsNullOrWhiteSpace(manifest.ToolchainVersion) ? "unspecified" : manifest.ToolchainVersion;
        if (allowInstalledRestore && string.Equals(manifest.ToolchainVersion, GpBundledToolchain.Version, StringComparison.Ordinal))
        {
            return new GpPrerequisite(HarnessName, "satisfied", $"this devkit restores .honua/toolchain on test (version {GpBundledToolchain.Version}). {NoSubmit}");
        }

        return new GpPrerequisite(
            HarnessName,
            "missing",
            $"project toolchain '{wanted}' does not match installed devkit '{GpBundledToolchain.Version}', and no local harness was found. Install the matching devkit or pass --harness-root. {NoSubmit}");
    }

    private static bool IsHarnessRoot(string candidate)
        => Directory.Exists(Path.Join(candidate, "docker", "worker-customcode-python", "harness"))
           && Directory.Exists(Path.Join(candidate, "docker", "worker-customcode-dotnet", "harness"));

    private static bool IsInsideProject(string projectDirectory, string relative, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            return false;
        }

        var segments = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Contains(".."))
        {
            return false;
        }

        var root = Path.GetFullPath(projectDirectory);
        full = Path.GetFullPath(Path.Join(root, relative));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.Ordinal);
    }
}
