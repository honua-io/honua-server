// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class HonuaAiIsolationTests
{
    private const string HonuaAiCsprojRelativePath = "src/Honua.Ai/Honua.Ai.csproj";

    [ArchitectureTest]
    public void HonuaAi_MustNotReference_HonuaServer()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var csprojPath = ArchitectureTestHelpers.CombinePath(repositoryRoot, HonuaAiCsprojRelativePath.Replace('/', Path.DirectorySeparatorChar));

        File.Exists(csprojPath).Should().BeTrue(
            "Honua.Ai.csproj must exist at the canonical path: {0}",
            csprojPath);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var serverReferences = new List<string>();

        CollectServerReferences(csprojPath, visited, serverReferences);

        serverReferences
            .Should()
            .BeEmpty(
                "Honua.Ai must never reference Honua.Server (directly or transitively). The whole " +
                "point of the ai-split carve is to flip that edge: Server depends on Ai, not the " +
                "reverse. Offenders: {0}",
                string.Join(", ", serverReferences));
    }

    [ArchitectureTest]
    public void HonuaAi_MustOnlyReference_PermittedProviders()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var csprojPath = ArchitectureTestHelpers.CombinePath(repositoryRoot, HonuaAiCsprojRelativePath.Replace('/', Path.DirectorySeparatorChar));

        var permittedProviders = new HashSet<string>(StringComparer.Ordinal)
        {
            "Honua.Core.Abstractions",
            "Honua.Core",
            "Honua.Geocoding",
            "Honua.Routing",
            "Honua.Hosting",
            "Honua.Jobs",
            "Honua.Geoprocessing",
            "Honua.ServiceDefaults",
        };

        var referenced = ArchitectureTestHelpers.DirectProjectReferenceNames(csprojPath);

        referenced
            .Should()
            .NotBeEmpty(
                "Honua.Ai must declare at least one ProjectReference (Core, Hosting, …) — empty " +
                "reference set indicates the csproj was rewritten without the expected scaffolding.");

        var disallowed = referenced
            .Where(name => !permittedProviders.Contains(name))
            .ToList();

        disallowed
            .Should()
            .BeEmpty(
                "Honua.Ai may only reference Honua.Core.Abstractions, Honua.Core, Honua.Geocoding, " +
                "Honua.Routing, Honua.Hosting, Honua.Jobs, Honua.Geoprocessing, and " +
                "Honua.ServiceDefaults. Disallowed references: {0}",
                string.Join(", ", disallowed));
    }

    private static void CollectServerReferences(
        string csprojPath,
        HashSet<string> visited,
        List<string> serverReferences)
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
            var referencedName = Path.GetFileNameWithoutExtension(referenced);

            if (string.Equals(referencedName, "Honua.Server", StringComparison.Ordinal))
            {
                serverReferences.Add(Path.GetRelativePath(
                    Path.GetDirectoryName(csprojPath) ?? string.Empty, referenced));
                continue;
            }

            CollectServerReferences(referenced, visited, serverReferences);
        }
    }
}
