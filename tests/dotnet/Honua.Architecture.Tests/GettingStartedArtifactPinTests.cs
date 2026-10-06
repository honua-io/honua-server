// Copyright 2025 Honua Authors
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;

namespace Honua.Architecture.Tests;

/// <summary>
/// Keeps the executable getting-started journey on the artifacts published for the release.
/// </summary>
public sealed class GettingStartedArtifactPinTests
{
    private const string CandidateImage = "ghcr.io/honua-io/honua-server@sha256:3ef3bd41a2f84d1f3a6194c11db496f741cc4d869b54bf57e9d7067dd9cf3d39";

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

        quickstart.Should().Contain(CandidateImage);
        quickstart.Should().Contain("Licensing__Mode: Disabled");
        linuxPackages.Should().Contain(CandidateImage);
        linuxPackages.Should().Contain("Licensing__Mode: Disabled");
        windowsPackages.Should().Contain(CandidateImage);
        windowsPackages.Should().Contain("Licensing__Mode: Disabled");
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
        journey.Should().NotContain("sha256:069f196bfa5c7201223d4d89868934242c4ace8805a6e48c122a88d84fa6eb1a");
        journey.Should().NotContain("curl ", "getting-started examples use the supported CLI, SDK, or MCP surfaces");
    }

    private static string Read(string root, params string[] path)
        => File.ReadAllText(ArchitectureTestHelpers.CombinePath([root, .. path]));
}
