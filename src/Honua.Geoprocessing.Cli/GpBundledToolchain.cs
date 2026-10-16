// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;

namespace Honua.Geoprocessing.Cli;

/// <summary>
/// The installed devkit carries the matching local harness and authoring SDK sources.
/// A scaffold extracts this versioned payload under .honua/toolchain; no server checkout
/// or unpublished package is required. Language runtimes remain developer prerequisites.
/// </summary>
internal static class GpBundledToolchain
{
    private const string Prefix = "Toolchain/";

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
        if (!Directory.Exists(destination))
        {
            GpCustomProjectScaffolder.Apply(root, Files());
        }

        return destination;
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

        return files;
    }
}
