// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Enforces the Honua.Import isolation contract: the carved-out data-ingest /
/// migration HTTP surface (Migration endpoints + job managers, file-import
/// upload plumbing, raster-import endpoints) must never take a ProjectReference
/// on Honua.Server, directly or transitively. Server references Import, never
/// the reverse.
/// </summary>
/// <remarks>
/// The provider-agnostic ingest <em>domain</em> (REST source clients,
/// abstractions, evidence model) deliberately stays in Honua.Core because the
/// storage providers consume it and must not pull this module's ASP.NET
/// surface; the NTS file-format readers stay in Honua.Geometry for the same
/// reason. This guard pins the upper (HTTP) half of that split.
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class HonuaImportIsolationTests
{
    private const string HonuaImportCsprojRelativePath = "src/Honua.Import/Honua.Import.csproj";

    private static readonly HashSet<string> PermittedProviders = new(StringComparer.Ordinal)
    {
        "Honua.Core.Abstractions",
        "Honua.Core",
        "Honua.Geometry",
        "Honua.Hosting",
        "Honua.ServiceDefaults",
    };

    [ArchitectureTest]
    public void HonuaImport_ShouldNotReference_HonuaServer()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var csprojPath = Path.Combine(repositoryRoot, HonuaImportCsprojRelativePath.Replace('/', Path.DirectorySeparatorChar));

        File.Exists(csprojPath).Should().BeTrue(
            "Honua.Import.csproj must exist at the canonical path: {0}", csprojPath);

        ReferencedProjectNames(csprojPath).Should().NotContain(
            "Honua.Server",
            "Honua.Import must never reference Honua.Server. The whole point of the import-split is " +
            "to flip that edge so Server depends on Import, not the reverse.");
    }

    [ArchitectureTest]
    public void HonuaImport_MustOnlyReference_PermittedProviders()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var csprojPath = Path.Combine(repositoryRoot, HonuaImportCsprojRelativePath.Replace('/', Path.DirectorySeparatorChar));

        var referenced = ReferencedProjectNames(csprojPath);

        referenced.Should().NotBeEmpty(
            "Honua.Import must declare at least one ProjectReference (Core, Hosting, …) — an empty " +
            "reference set indicates the csproj was rewritten without the expected scaffolding.");

        var disallowed = referenced.Where(name => !PermittedProviders.Contains(name)).ToList();

        disallowed.Should().BeEmpty(
            "Honua.Import may only reference Honua.Core.Abstractions, Honua.Core, Honua.Geometry, " +
            "Honua.Hosting, and Honua.ServiceDefaults. A reference to a storage provider would be a " +
            "tier violation (the ingest domain that providers share lives in Core). Disallowed " +
            "references: {0}",
            string.Join(", ", disallowed));
    }

    private static IReadOnlyList<string> ReferencedProjectNames(string csprojPath)
        => ArchitectureTestHelpers.DirectProjectReferenceNames(csprojPath);
}
