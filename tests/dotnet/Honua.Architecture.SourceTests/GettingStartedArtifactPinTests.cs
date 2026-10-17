// Copyright 2025 Honua Authors
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;

namespace Honua.Architecture.Tests;

/// <summary>
/// Keeps the executable getting-started journey on the documented published preview artifacts.
/// </summary>
public sealed class GettingStartedArtifactPinTests
{
    // Local evaluation uses the published preview until release promotion publishes
    // the RC channel. Preview identity and observations live in the linked receipt;
    // the docs must never turn an unavailable release channel into an install default.
    private const string PreviewImage = "ghcr.io/honua-io/honua-server:nightly-0e03bdd";
    private const string ReleaseChannelImage = "ghcr.io/honua-io/honua-server:2026.1-rc";
    private const string GaChannelImage = "`ghcr.io/honua-io/honua-server:2026.1`";
    private const string PreviewReceipt = "../reference/local-preview-artifact.md";
    private const string InstallManifest = "customer-install-manifest.json";
    private const string ServerImageDigestPrefix = "ghcr.io/honua-io/honua-server@sha256:";

    [ArchitectureTest]
    public void GettingStartedPages_PinPublishedClientAndServerArtifacts()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var quickstart = Read(root, "docs", "get-started", "quickstart.md");
        var firstDataset = Read(root, "docs", "get-started", "first-dataset.md");
        var firstMap = Read(root, "docs", "get-started", "first-map.md");
        var linuxPackages = Read(root, "docs", "get-started", "linux-packages.md");
        var registryClients = Read(root, "docs", "get-started", "registry-clients.md");
        var windowsPackages = Read(root, "docs", "get-started", "windows-packages.md");
        var dotnetGettingStarted = Read(root, "docs", "sdks", "dotnet", "dotnet-getting-started.md");

        foreach (var page in new[] { quickstart, linuxPackages, windowsPackages })
        {
            page.Should().Contain(ReleaseChannelImage);
            page.Should().Contain(GaChannelImage);
            page.Should().Contain(PreviewReceipt);
            page.Should().Contain(InstallManifest);
            page.Should().Contain("Licensing__Mode: Disabled");
            page.Should().Contain("HONUA_IMAGE=" + PreviewImage);
            page.Should().NotContain("HONUA_IMAGE=" + ReleaseChannelImage);
        }

        var gettingStartedDir = ArchitectureTestHelpers.CombinePath(root, "docs", "get-started");
        foreach (var file in Directory.EnumerateFiles(gettingStartedDir, "*.md", SearchOption.AllDirectories))
        {
            File.ReadAllText(file).Should().NotContain("@sha256:", Path.GetFileName(file));
        }
        firstDataset.Should().Contain("@honua/sdk-js@0.1.16");
        registryClients.Should().Contain("`@honua/sdk-js@0.1.16`");
        registryClients.Should().Contain("`create-honua-app@0.1.9`");
        registryClients.Should().Contain("`Honua.Sdk` `1.11.1`");
        registryClients.Should().Contain("`Honua.Sdk.Admin` `1.11.1`");
        dotnetGettingStarted.Should().Contain("Honua.Sdk --version 1.11.1");

        var journey = string.Join('\n', quickstart, firstDataset, firstMap, linuxPackages,
            registryClients, windowsPackages, dotnetGettingStarted);
        journey.Should().Contain("honua-sdk==0.1.16");
        journey.Should().Contain("honua-admin==0.1.12");
        journey.Should().NotContain("@sha256:",
            "no getting-started page hand-copies an image digest");
        journey.Should().NotContain(ServerImageDigestPrefix,
            "the release digest lives in customer-install-manifest.json, not in these pages");
        journey.Should().NotContain("honua-server/manifests/sha256:",
            "a hand-copied registry manifest link is a server image digest pin");
        journey.Should().NotContain("curl ", "getting-started examples use the supported CLI, SDK, or MCP surfaces");
    }

    private static string Read(string root, params string[] path)
        => File.ReadAllText(ArchitectureTestHelpers.CombinePath([root, .. path]));
}
