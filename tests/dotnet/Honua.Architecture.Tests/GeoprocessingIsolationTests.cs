// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Enforces the Honua.Geoprocessing isolation contract: the geoprocessing
/// subsystem is carved out of Honua.Server so the remaining Phase 1 protocol
/// extractions (OgcApi.Processes, GeoServices/GPServer) can be split without
/// transitively pulling Geoprocessing through Honua.Server. The carve only
/// holds if Honua.Geoprocessing itself does not back-reference Honua.Server.
/// </summary>
/// <remarks>
/// Pinning the ProjectReference closure at the csproj layer (rather than
/// scanning IL) catches the policy violation at PR time even before the
/// downstream protocol extractions land.
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class GeoprocessingIsolationTests
{
    private static readonly string[] BannedProjectReferences =
    {
        "Honua.Server",
        "Honua.Postgres",
    };

    [ArchitectureTest]
    public void HonuaGeoprocessingCsproj_ShouldNotReference_ServerOrPostgres()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var geoprocessingCsproj = ArchitectureTestHelpers.CombinePath(
            repositoryRoot,
            "src",
            "Honua.Geoprocessing",
            "Honua.Geoprocessing.csproj");

        File.Exists(geoprocessingCsproj).Should().BeTrue(
            "the Honua.Geoprocessing csproj must exist at the canonical carve path: {0}",
            geoprocessingCsproj);

        var projectReferences = ArchitectureTestHelpers.DirectProjectReferenceNames(geoprocessingCsproj);

        var offending = projectReferences
            .Where(name => BannedProjectReferences.Contains(name, StringComparer.Ordinal))
            .ToList();

        offending
            .Should()
            .BeEmpty(
                "Honua.Geoprocessing was carved out of Honua.Server so the geoprocessing " +
                "surface can be referenced by the remaining Phase 1 protocol extractions " +
                "(OgcApi.Processes, GeoServices/GPServer) without pulling Honua.Server " +
                "transitively, and provider-neutral raster execution must not pull Postgres " +
                "SQL/client types into the GP layer. A back-reference to Honua.Server or " +
                "Honua.Postgres would violate that dependency direction. Move the contract " +
                "into Honua.Core and keep provider implementation in Honua.Postgres, or use " +
                "Honua.Hosting / Honua.Jobs / Honua.Core.Abstractions, or extract a new " +
                "lower-tier helper assembly.");
    }
}
