// Copyright 2025 Honua Authors
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;

namespace Honua.Architecture.Tests;

/// <summary>
/// Keeps the executable getting-started journey on the artifacts published for the release.
/// </summary>
public sealed class GettingStartedArtifactPinTests
{
    // The getting-started pages reference the release channel, which only release promotion
    // moves (ruling R18). The exact digest is in the release's customer-install-manifest.json
    // (`server.image`); it is never hand-copied into these pages.
    private const string ReleaseChannelImage = "ghcr.io/honua-io/honua-server:2026.1-rc";
    private const string GaChannelImage = "`ghcr.io/honua-io/honua-server:2026.1`";
    private const string DigestSentence = "The exact digest is in the release's `customer-install-manifest.json`";
    private const string DigestSource = "`server.image` in that release's [customer install manifest](https://honua.io/data/customer-install-manifest.json)";
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

        foreach (var page in new[] { quickstart, linuxPackages })
        {
            page.Should().Contain(ReleaseChannelImage);
            page.Should().Contain(GaChannelImage);
            page.Should().Contain(DigestSentence);
            page.Should().Contain(DigestSource);
            page.Should().Contain("Licensing__Mode: Disabled");
        }

        var gettingStartedDir = ArchitectureTestHelpers.CombinePath(root, "docs", "get-started");
        foreach (var file in Directory.EnumerateFiles(gettingStartedDir, "*.md", SearchOption.AllDirectories))
        {
            File.ReadAllText(file).Should().NotContain("@sha256:", Path.GetFileName(file));
        }

        quickstart.Should().Contain("HONUA_IMAGE=" + ReleaseChannelImage);
        linuxPackages.Should().Contain("HONUA_IMAGE=" + ReleaseChannelImage);
        // The Windows package page is outside this change and still names the older channel tag.
        windowsPackages.Should().Contain("$Image = 'ghcr.io/honua-io/honua-server:rc'");
        firstDataset.Should().Contain("@honua/sdk-js@0.1.13");
        registryClients.Should().Contain("`@honua/sdk-js@0.1.13`");
        registryClients.Should().Contain("`create-honua-app@0.1.6`");
        registryClients.Should().Contain("`Honua.Sdk` `1.10.3`");
        registryClients.Should().Contain("`Honua.Sdk.Admin` `1.10.3`");
        dotnetGettingStarted.Should().Contain("Honua.Sdk --version 1.10.3");

        var journey = string.Join('\n', quickstart, firstDataset, firstMap, linuxPackages,
            registryClients, windowsPackages, dotnetGettingStarted);
        journey.Should().Contain("honua-sdk==0.1.13");
        journey.Should().Contain("honua-admin==0.1.10");
        journey.Should().NotContain("@sha256:",
            "no getting-started page hand-copies an image digest");
        journey.Should().NotContain(ServerImageDigestPrefix,
            "the server image digest lives in the release's customer-install-manifest.json, not in these pages");
        journey.Should().NotContain("honua-server/manifests/sha256:",
            "a hand-copied registry manifest link is a server image digest pin");
        journey.Should().NotContain("curl ", "getting-started examples use the supported CLI, SDK, or MCP surfaces");
    }

    private static string Read(string root, params string[] path)
        => File.ReadAllText(ArchitectureTestHelpers.CombinePath([root, .. path]));
}
