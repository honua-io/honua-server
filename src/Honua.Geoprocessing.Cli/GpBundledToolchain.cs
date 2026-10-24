// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Text;

namespace Honua.Geoprocessing.Cli;

/// <summary>
/// The installed devkit carries the matching local harness and authoring SDK sources.
/// A scaffold extracts this versioned payload under .honua/toolchain; no server checkout
/// or unpublished package is required. Language runtimes remain developer prerequisites.
/// </summary>
internal static class GpBundledToolchain
{
    private const string Prefix = "Toolchain/";
    internal const string VersionFileName = "toolchain-version.txt";

    internal static string Version => typeof(GpBundledToolchain).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    internal static string Restore(string projectDirectory, string? version)
    {
        if (!string.Equals(version, Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The project needs devkit toolchain '{version ?? "unspecified"}', but this installed devkit carries '{Version}'. Install the matching devkit or use an explicit source-development --harness-root.");
        }

        var root = Path.GetFullPath(projectDirectory);
        var destination = Path.Join(root, ".honua", "toolchain");
        if (!IsCurrent(destination))
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            GpCustomProjectScaffolder.Apply(root, Files());
        }

        return destination;
    }

    /// <summary>
    /// True when <paramref name="toolchainDirectory"/> is the payload extracted by this devkit.
    /// Harness directories alone are not enough: a leftover tree from another version is stale.
    /// </summary>
    internal static bool IsCurrent(string toolchainDirectory)
    {
        var stampPath = Path.Join(toolchainDirectory, VersionFileName);
        if (!File.Exists(stampPath))
        {
            return false;
        }

        var recorded = File.ReadAllText(stampPath).Trim();
        return string.Equals(recorded, Version, StringComparison.Ordinal)
            && Directory.Exists(Path.Join(toolchainDirectory, "docker", "worker-customcode-python", "harness"))
            && Directory.Exists(Path.Join(toolchainDirectory, "docker", "worker-customcode-dotnet", "harness"));
    }

    internal static IReadOnlyList<GpProjectFile> Files()
    {
        var assembly = typeof(GpBundledToolchain).Assembly;
        var files = new List<GpProjectFile>();
        foreach (var resource in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal))
        {
            var normalized = resource.Replace('\\', '/');
            if (!normalized.StartsWith(Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            using var input = assembly.GetManifestResourceStream(resource)!;
            using var output = new MemoryStream();
            input.CopyTo(output);
            files.Add(new GpProjectFile(".honua/toolchain/" + normalized[Prefix.Length..], output.ToArray()));
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException("The devkit package has no bundled authoring toolchain.");
        }

        files.Add(new GpProjectFile(
            ".honua/toolchain/" + VersionFileName,
            Encoding.UTF8.GetBytes(Version + "\n")));
        return files;
    }
}
