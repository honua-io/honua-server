// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;

namespace Honua.Geoprocessing.Cli;

/// <summary>
/// Reports whether Docker, kind, kubectl, and Helm are on PATH. A missing tool is
/// reported with one remediation line. It does not decide <c>honua-gp-devkit doctor</c>'s
/// exit code: that stays the managed probe plus, unless <c>--managed-only</c>, the native image.
/// </summary>
internal static class GpHarnessPrerequisiteProbe
{
    internal const string Found = "found";
    internal const string Missing = "missing";

    /// <summary>One toolchain the kind lifecycle check needs, and how to install it.</summary>
    /// <param name="Name">The executable name (<c>docker</c>, <c>kind</c>, <c>kubectl</c>, or <c>helm</c>).</param>
    /// <param name="Status"><see cref="Found"/> or <see cref="Missing"/>.</param>
    /// <param name="Remediation">One line telling the developer what to install. Never empty.</param>
    internal readonly record struct ToolReport(string Name, string Status, string Remediation)
    {
        /// <summary>JSON field that carries <see cref="Remediation"/>.</summary>
        public string RemediationField => Name + "Remediation";
    }

    /// <summary>
    /// Looks up the four harness tools. <paramref name="pathLookup"/> returns the executable
    /// path, or null when it is absent. Tests pass a delegate; doctor uses <see cref="DefaultPathLookup"/>.
    /// </summary>
    /// <param name="pathLookup">Path lookup, or null for the process PATH.</param>
    /// <returns>Docker, kind, kubectl, and Helm, in that order.</returns>
    internal static IReadOnlyList<ToolReport> Probe(Func<string, string?>? pathLookup = null)
    {
        var lookup = pathLookup ?? DefaultPathLookup;
        return
        [
            Report("docker", lookup, "Install Docker Engine and the Compose v2 plugin, then rerun honua-gp-devkit doctor. Missing docker is reported and does not fail doctor."),
            Report("kind", lookup, "Install kind from https://kind.sigs.k8s.io/docs/user/quick-start/, then rerun python3 scripts/ci/gp-kind-lifecycle-matrix.py. Missing kind is reported and does not fail doctor."),
            Report("kubectl", lookup, "Install kubectl matching the kind cluster, then rerun python3 scripts/ci/gp-kind-lifecycle-matrix.py. Missing kubectl is reported and does not fail doctor."),
            Report("helm", lookup, "Install Helm 3 from https://helm.sh/docs/intro/install/, then rerun python3 scripts/ci/gp-kind-lifecycle-matrix.py. Missing helm is reported and does not fail doctor."),
        ];
    }

    /// <summary>Writes <c>docker</c>/<c>dockerRemediation</c> (and kind, kubectl, helm) onto a doctor report.</summary>
    /// <param name="report">The doctor JSON object.</param>
    /// <param name="pathLookup">Path lookup, or null for the process PATH.</param>
    internal static void WriteJson(JsonObject report, Func<string, string?>? pathLookup = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        foreach (var tool in Probe(pathLookup))
        {
            report[tool.Name] = tool.Status;
            report[tool.RemediationField] = tool.Remediation;
        }
    }

    /// <summary>Human-readable status plus one remediation line per tool.</summary>
    /// <param name="pathLookup">Path lookup, or null for the process PATH.</param>
    /// <returns>Eight lines: status, remediation, repeated for each tool.</returns>
    internal static string FormatReport(Func<string, string?>? pathLookup = null)
    {
        var lines = new List<string>(8);
        foreach (var tool in Probe(pathLookup))
        {
            lines.Add($"{tool.Name}: {tool.Status}");
            lines.Add(tool.Remediation);
        }

        return string.Join('\n', lines);
    }

    /// <summary>Resolves <paramref name="name"/> against PATH without spawning it.</summary>
    /// <param name="name">A bare executable name.</param>
    /// <returns>The first matching path, or null.</returns>
    internal static string? DefaultPathLookup(string name)
        => LookupOnPath(name, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());

    /// <summary>
    /// Resolves <paramref name="name"/> on <paramref name="path"/>. On Windows the candidates are
    /// <c>.exe</c>, <c>.cmd</c>, and <c>.bat</c>, matching <c>GpRunnability</c> and <c>GpCustomProject</c>.
    /// Elsewhere the name is used as given.
    /// </summary>
    /// <param name="name">A bare executable name.</param>
    /// <param name="path">A PATH value, or null.</param>
    /// <param name="windowsExecutableExtensions">Whether to probe Windows executable extensions.</param>
    /// <returns>The first matching path, or null.</returns>
    internal static string? LookupOnPath(string name, string? path, bool windowsExecutableExtensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Path.IsPathRooted(name) || name.Contains('/') || name.Contains('\\'))
        {
            return null;
        }

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var names = windowsExecutableExtensions
            ? new[] { name + ".exe", name + ".cmd", name + ".bat" }
            : new[] { name };
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidateName in names)
            {
                try
                {
                    // Join does not drop earlier segments when a later one is rooted.
                    var candidate = Path.Join(directory, candidateName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    continue;
                }
            }
        }

        return null;
    }

    private static ToolReport Report(string name, Func<string, string?> pathLookup, string remediation)
    {
        var located = pathLookup(name);
        var status = string.IsNullOrWhiteSpace(located) ? Missing : Found;
        return new ToolReport(name, status, remediation);
    }
}
