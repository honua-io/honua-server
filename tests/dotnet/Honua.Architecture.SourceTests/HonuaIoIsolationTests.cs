// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class HonuaIoIsolationTests
{
    private const string HonuaIoCsprojRelativePath = "src/Honua.Io/Honua.Io.csproj";

    private static readonly HashSet<string> PermittedProviders = new(StringComparer.Ordinal)
    {
        "Honua.Core.Abstractions",
        "Honua.Core",
        "Honua.Geometry",
        "Honua.Hosting",
        "Honua.ServiceDefaults",
        // Lean plugin contract surface (issue #2856, ADR-0067): the export endpoint consumes
        // IFeatureOutputFormatRegistry to serve plugin-contributed feature output formats and
        // expresses the built-in CSV writer as an IFeatureOutputFormat. This is the dependency-
        // minimal Abstractions package (never the host-side Honua.Plugins runtime), mirroring how
        // protocol assemblies depend on IPluginEditPipeline.
        "Honua.Plugins.Abstractions",
    };

    [ArchitectureTest]
    public void HonuaIo_ShouldNotReference_HonuaServer()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var csprojPath = ArchitectureTestHelpers.CombinePath(repositoryRoot, HonuaIoCsprojRelativePath.Replace('/', Path.DirectorySeparatorChar));

        File.Exists(csprojPath).Should().BeTrue(
            "Honua.Io.csproj must exist at the canonical path: {0}", csprojPath);

        var referenced = ReferencedProjectNames(csprojPath);

        referenced.Should().NotContain(
            "Honua.Server",
            "Honua.Io must never reference Honua.Server. The whole point of the io-split is to " +
            "flip that edge so Server depends on Io, not the reverse.");
    }

    [ArchitectureTest]
    public void HonuaIo_MustOnlyReference_PermittedProviders()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var csprojPath = ArchitectureTestHelpers.CombinePath(repositoryRoot, HonuaIoCsprojRelativePath.Replace('/', Path.DirectorySeparatorChar));

        var referenced = ReferencedProjectNames(csprojPath);

        referenced.Should().NotBeEmpty(
            "Honua.Io must declare at least one ProjectReference (Core, Hosting, …) — an empty " +
            "reference set indicates the csproj was rewritten without the expected scaffolding.");

        var disallowed = referenced.Where(name => !PermittedProviders.Contains(name)).ToList();

        disallowed.Should().BeEmpty(
            "Honua.Io may only reference Honua.Core.Abstractions, Honua.Core, Honua.Geometry, " +
            "Honua.Hosting, and Honua.ServiceDefaults. Disallowed references: {0}",
            string.Join(", ", disallowed));
    }

    private static IReadOnlyList<string> ReferencedProjectNames(string csprojPath)
        => ArchitectureTestHelpers.DirectProjectReferenceNames(csprojPath);
}
