// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Enforces the Honua.Ai isolation contract: the carved-out AI surface
/// (AiBuilder + Grounding + NlQuery + AnalysisContent) must never take a
/// ProjectReference on Honua.Server, even transitively. The whole reason
/// Honua.Ai exists is to flip that edge: today Server references Ai (the
/// composition root pulls the carved feature in), and that direction must
/// stay one-way.
/// </summary>
/// <remarks>
/// <para>
/// Mirror of <see cref="CloudSdkIsolationTests"/> in shape but pinned to
/// the Ai cluster's specific failure mode. A regression here would mean
/// the AI feature surface started reaching back into the composition root
/// (re-introducing the cycle ADR-0047 forbids).
/// </para>
/// <para>
/// See <c>docs/contributor/adr/0047-module-dependency-policy.md</c> for
/// the module-dependency policy this guard sits inside.
/// </para>
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class HonuaAiIsolationTests
{
    private const string HonuaAiCsprojRelativePath = "src/Honua.Ai/Honua.Ai.csproj";

    [ArchitectureTest]
    public void HonuaAi_MustNotReference_HonuaServer()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var csprojPath = Path.Combine(repositoryRoot, HonuaAiCsprojRelativePath.Replace('/', Path.DirectorySeparatorChar));

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

    /// <summary>
    /// Asserts the ProjectReference set declared by Honua.Ai stays inside the
    /// canonical allow-list (Abstractions, Core, Hosting, Jobs, Geoprocessing,
    /// ServiceDefaults). New references — to a storage provider, a protocol
    /// module, or any other satellite — must come with a deliberate matrix
    /// update in ADR-0047 / <see cref="ModuleDependencyPolicyTests"/>.
    /// </summary>
    [ArchitectureTest]
    public void HonuaAi_MustOnlyReference_PermittedProviders()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var csprojPath = Path.Combine(repositoryRoot, HonuaAiCsprojRelativePath.Replace('/', Path.DirectorySeparatorChar));

        var permittedProviders = new HashSet<string>(StringComparer.Ordinal)
        {
            "Honua.Core.Abstractions",
            "Honua.Core",
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
                "Honua.Ai may only reference Honua.Core.Abstractions, Honua.Core, Honua.Hosting, " +
                "Honua.Jobs, Honua.Geoprocessing, and Honua.ServiceDefaults. Disallowed references: {0}",
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
            var referenced = Path.GetFullPath(Path.Combine(csprojDir, normalizedInclude));
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
