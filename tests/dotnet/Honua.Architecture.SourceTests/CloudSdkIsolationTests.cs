// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class CloudSdkIsolationTests
{
    private static readonly string[] BannedPackagePrefixes =
    {
        "AWSSDK.",
        "Azure.",
    };

    public static IEnumerable<object[]> CloudNeutralProjects()
    {
        yield return new object[] { ArchitectureTestHelpers.CombinePath("src", "Honua.Core", "Abstractions", "Honua.Core.Abstractions.csproj") };
        yield return new object[] { ArchitectureTestHelpers.CombinePath("src", "Honua.Core", "Core", "Honua.Core.csproj") };
        yield return new object[] { ArchitectureTestHelpers.CombinePath("src", "Honua.Hosting", "Honua.Hosting.csproj") };
    }

    [Theory]
    [Trait("Category", "Architecture")]
    [MemberData(nameof(CloudNeutralProjects))]
    public void CloudNeutralProject_MustNotTransitivelyReference_AwsOrAzurePackages(string relativeCsprojPath)
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var entryCsproj = ArchitectureTestHelpers.CombinePath(repositoryRoot, relativeCsprojPath);

        File.Exists(entryCsproj).Should().BeTrue(
            "the upstream cloud-neutral csproj must exist at the canonical path: {0}",
            entryCsproj);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offending = new List<(string Csproj, string Package)>();

        CollectBannedPackageRefs(entryCsproj, visited, offending);

        offending
            .Should()
            .BeEmpty(
                "Honua.Core / Honua.Core.Abstractions / Honua.Hosting are upstream of the AWS/Azure carve-out and must not pull AWSSDK.* / Azure.* packages even transitively. Offenders: {0}",
                string.Join(", ", offending.Select(pair => $"{Path.GetFileName(pair.Csproj)} -> {pair.Package}")));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void HonuaServer_MustNotImport_AwsOrAzureSdkNamespaces()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var serverRoot = ArchitectureTestHelpers.CombinePath(repositoryRoot, "src", "Honua.Server");

        Directory.Exists(serverRoot).Should().BeTrue(
            "Honua.Server source root must exist at: {0}",
            serverRoot);

        var offenders = new List<(string File, string UsingLine)>();
        foreach (var file in Directory.EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Skip generated obj/ output so a stale build doesn't trigger the guard.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var bannedUsings = File.ReadLines(file)
                .Select(line => line.TrimStart())
                .Where(trimmed => trimmed.StartsWith("using Amazon.", StringComparison.Ordinal)
                    || trimmed.StartsWith("using Azure.", StringComparison.Ordinal)
                    || trimmed.StartsWith("using static Amazon.", StringComparison.Ordinal)
                    || trimmed.StartsWith("using static Azure.", StringComparison.Ordinal));

            foreach (var trimmed in bannedUsings)
            {
                offenders.Add((file, trimmed));
            }
        }

        offenders.Should().BeEmpty(
            "Honua.Server must not directly import AWSSDK / Azure SDK namespaces — move the SDK-typed code into Honua.Aws / Honua.Azure (which preserve the Honua.Server.Features.* namespace) or expose a small cloud-neutral abstraction from Honua.Core / Honua.Hosting. Offenders: {0}",
            string.Join("; ", offenders.Select(pair => $"{Path.GetFileName(pair.File)}: {pair.UsingLine}")));
    }

    private static void CollectBannedPackageRefs(
        string csprojPath,
        HashSet<string> visited,
        List<(string Csproj, string Package)> offending)
    {
        var normalized = Path.GetFullPath(csprojPath);
        if (!visited.Add(normalized))
        {
            return;
        }

        if (!File.Exists(normalized))
        {
            return;
        }

        var document = XDocument.Load(normalized);

        foreach (var package in document
            .Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Where(package => BannedPackagePrefixes.Any(prefix =>
                package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
        {
            offending.Add((normalized, package));
        }

        var csprojDir = Path.GetDirectoryName(normalized)
            ?? throw new InvalidOperationException(
                $"Unable to resolve directory for csproj: {normalized}");

        foreach (var projectInclude in document
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!))
        {
            var normalizedInclude = projectInclude.Replace('\\', Path.DirectorySeparatorChar);
            var referenced = Path.GetFullPath(ArchitectureTestHelpers.CombinePath(csprojDir, normalizedInclude));
            CollectBannedPackageRefs(referenced, visited, offending);
        }
    }
}
