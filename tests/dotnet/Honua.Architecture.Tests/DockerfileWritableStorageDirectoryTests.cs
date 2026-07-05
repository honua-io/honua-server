// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards the demo-render contract (honua-server#2311): the runtime image is built for a
/// read-only root filesystem (<c>security.read-only-root="true"</c>), so every directory the
/// default configuration writes to at runtime MUST be provisioned as a writable runtime
/// directory in the Dockerfile. When the two file-storage directories were omitted, inline
/// map-image responses (<c>f=image</c>, which stream bytes directly) still worked while every
/// <c>href</c>/<c>f=json</c> export response — MapServer <c>export</c>, ImageServer
/// <c>exportImage</c>, OGC API Maps — failed with a 500, because persisting the rendered image
/// to <c>TemporaryFiles:StorageDirectory</c> threw on the read-only path. This test keeps the
/// Dockerfile's provisioned writable directories in sync with the default storage directories
/// declared in appsettings.json so that regression cannot recur silently.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class DockerfileWritableStorageDirectoryTests
{
    private const string DockerfileRelativePath = "Dockerfile";
    private const string AppSettingsRelativePath = "src/Honua.Server/appsettings.json";

    [ArchitectureTest]
    public void Dockerfile_ProvisionsEveryDefaultLocalStorageDirectory()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var dockerfilePath = Path.Combine(repositoryRoot, DockerfileRelativePath);
        var appSettingsPath = Path.Combine(
            repositoryRoot,
            AppSettingsRelativePath.Replace('/', Path.DirectorySeparatorChar));

        File.Exists(dockerfilePath).Should().BeTrue(
            "the runtime Dockerfile must exist at the canonical path: {0}", dockerfilePath);
        File.Exists(appSettingsPath).Should().BeTrue(
            "the server appsettings.json must exist at the canonical path: {0}", appSettingsPath);

        var dockerfile = File.ReadAllText(dockerfilePath);
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

        requiredDirectories.Should().NotBeEmpty(
            "appsettings.json must declare the default local storage directories this test protects.");

        foreach (var (configPath, directory) in requiredDirectories)
        {
            IsProvisionedInDockerfile(dockerfile, directory).Should().BeTrue(
                "the default storage directory '{0}' ({1}) must be provisioned as a writable runtime " +
                "directory in the Dockerfile (mkdir -p + chown to the runtime user). The image runs with " +
                "a read-only root filesystem, so any unprovisioned path is read-only and every rendered " +
                "map-image export (href/f=json) that persists to it fails with a 500 (honua-server#2311).",
                directory,
                configPath);
        }
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
        foreach (var rawLine in dockerfile.Split('\n'))
        {
            var line = rawLine.Trim();
            var directiveIndex = line.IndexOf(directive, StringComparison.Ordinal);
            if (directiveIndex < 0)
            {
                continue;
            }

            foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("/tmp/", StringComparison.Ordinal))
                {
                    tokens.Add(token);
                }
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
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
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
