// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards the demo-render contract (honua-server#2311): a runtime image built for a
/// read-only root filesystem (<c>security.read-only-root="true"</c>) makes every path that
/// is not explicitly provisioned (or volume-mounted by the deployment) read-only, so every
/// directory the default configuration writes to at runtime MUST be provisioned as a
/// writable runtime directory in that image's Dockerfile. When the two file-storage
/// directories were omitted, inline map-image responses (<c>f=image</c>, which stream bytes
/// directly) still worked while every <c>href</c>/<c>f=json</c> export response — MapServer
/// <c>export</c>, ImageServer <c>exportImage</c> — failed with a 500, because persisting the
/// rendered image to <c>TemporaryFiles:StorageDirectory</c> threw on the read-only path.
/// This test discovers every Dockerfile in the repository that declares the read-only-root
/// label and keeps its provisioned writable directories in sync with the default storage
/// directories declared in appsettings.json, so the regression cannot recur silently in any
/// read-only-root image (the container Dockerfile and docker/Dockerfile.aot today).
/// </summary>
[Trait("Category", "Architecture")]
public sealed class DockerfileWritableStorageDirectoryTests
{
    private const string ReadOnlyRootLabel = "security.read-only-root=\"true\"";
    private const string AppSettingsRelativePath = "src/Honua.Server/appsettings.json";

    [ArchitectureTest]
    public void ReadOnlyRootDockerfiles_ProvisionEveryDefaultLocalStorageDirectory()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var appSettingsPath = ArchitectureTestHelpers.CombinePath(
            repositoryRoot,
            AppSettingsRelativePath.Replace('/', Path.DirectorySeparatorChar));

        File.Exists(appSettingsPath).Should().BeTrue(
            "the server appsettings.json must exist at the canonical path: {0}", appSettingsPath);

        var requiredDirectories = ReadRequiredStorageDirectories(appSettingsPath);
        requiredDirectories.Should().NotBeEmpty(
            "appsettings.json must declare the default local storage directories this test protects.");

        var readOnlyRootDockerfiles = DiscoverReadOnlyRootDockerfiles(repositoryRoot);
        readOnlyRootDockerfiles.Should().NotBeEmpty(
            "at least the root container Dockerfile declares {0}; if the label moved, update this test.",
            ReadOnlyRootLabel);

        foreach (var dockerfilePath in readOnlyRootDockerfiles)
        {
            var dockerfile = File.ReadAllText(dockerfilePath);
            var relativePath = Path.GetRelativePath(repositoryRoot, dockerfilePath);

            foreach (var (configPath, directory) in requiredDirectories)
            {
                IsProvisionedInDockerfile(dockerfile, directory).Should().BeTrue(
                    "the default storage directory '{0}' ({1}) must be provisioned as a writable runtime " +
                    "directory in '{2}' (mkdir -p + chown to the runtime user). That image declares a " +
                    "read-only root filesystem, so any unprovisioned path is read-only and every rendered " +
                    "map-image export (href/f=json) that persists to it fails with a 500 (honua-server#2311).",
                    directory,
                    configPath,
                    relativePath);
            }
        }
    }

    /// <summary>
    /// Finds every Dockerfile in the repository that declares the read-only-root security
    /// label. Scans the repo root and the docker/ directory (where all runtime image
    /// definitions live) rather than hardcoding file names, so a new read-only-root image
    /// is covered automatically.
    /// </summary>
    private static List<string> DiscoverReadOnlyRootDockerfiles(string repositoryRoot)
    {
        var candidates = new List<string>();

        var rootDockerfile = ArchitectureTestHelpers.CombinePath(repositoryRoot, "Dockerfile");
        if (File.Exists(rootDockerfile))
        {
            candidates.Add(rootDockerfile);
        }

        var dockerDirectory = ArchitectureTestHelpers.CombinePath(repositoryRoot, "docker");
        if (Directory.Exists(dockerDirectory))
        {
            candidates.AddRange(Directory.EnumerateFiles(dockerDirectory, "Dockerfile*", SearchOption.TopDirectoryOnly));
        }

        return candidates
            .Where(path => File.ReadAllText(path).Contains(ReadOnlyRootLabel, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static List<(string ConfigPath, string Directory)> ReadRequiredStorageDirectories(string appSettingsPath)
    {
        using var appSettings = JsonDocument.Parse(File.ReadAllText(appSettingsPath));
        var root = appSettings.RootElement;

        var requiredDirectories = new List<(string ConfigPath, string Directory)>();

        if (TryReadStringPath(root, out var tempDir, "TemporaryFiles", "StorageDirectory") &&
            IsLocalContainerPath(tempDir))
        {
            requiredDirectories.Add(("TemporaryFiles:StorageDirectory", tempDir));
        }

        if (TryReadStringPath(root, out var storageBasePath, "FileStorage", "LocalStorage", "BasePath") &&
            IsLocalContainerPath(storageBasePath))
        {
            requiredDirectories.Add(("FileStorage:LocalStorage:BasePath", storageBasePath));
        }

        return requiredDirectories;
    }

    // A path is provisioned when it appears both in a `mkdir -p` invocation and a `chown`
    // invocation in the Dockerfile. Matching on the raw path token (surrounded by whitespace or
    // line boundaries) is sufficient because these directories are absolute and unique.
    private static bool IsProvisionedInDockerfile(string dockerfile, string directory)
    {
        var createdDirectories = CollectTokensFromDirective(dockerfile, "mkdir");
        var ownedDirectories = CollectTokensFromDirective(dockerfile, "chown");
        return createdDirectories.Contains(directory) && ownedDirectories.Contains(directory);
    }

    private static HashSet<string> CollectTokensFromDirective(string dockerfile, string directive)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in dockerfile.Split('\n').Select(rawLine => rawLine.Trim()))
        {
            var directiveIndex = line.IndexOf(directive, StringComparison.Ordinal);
            if (directiveIndex < 0)
            {
                continue;
            }

            foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.StartsWith("/tmp/", StringComparison.Ordinal)))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private static bool IsLocalContainerPath(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.StartsWith("/tmp/", StringComparison.Ordinal);

    private static bool TryReadStringPath(JsonElement root, out string value, params string[] path)
    {
        value = string.Empty;
        var current = root;
        // Not a filter: each iteration threads the mutated `current` (TryGetProperty's out
        // parameter) into the next, so this is a stateful traversal/fold with early exit, not
        // an expression `.Where(...)` could represent — the cs/linq/missed-where note does not
        // apply here.
        foreach (var segment in path)
        {
            switch (current.ValueKind == JsonValueKind.Object &&
                    current.TryGetProperty(segment, out current))
            {
                case false:
                    return false;
            }
        }

        if (current.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = current.GetString() ?? string.Empty;
        return true;
    }
}
