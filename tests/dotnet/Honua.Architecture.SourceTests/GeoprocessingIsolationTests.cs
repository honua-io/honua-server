// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class GeoprocessingIsolationTests
{
    private static readonly string[] BannedProjectReferences =
    {
        "Honua.Server",
    };

    [ArchitectureTest]
    public void HonuaGeoprocessingCsproj_ShouldNotReference_HonuaServer()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var geoprocessingCsproj = ArchitectureTestHelpers.CombinePath(
            repositoryRoot,
            "src",
            "Honua.Geoprocessing", "Core",
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
                "transitively. A back-reference to Honua.Server would re-introduce the cycle " +
                "the carve was designed to break. Move the dependency into Honua.Core / " +
                "Honua.Hosting / Honua.Jobs / Honua.Core.Abstractions, or extract a new " +
                "lower-tier helper assembly.");
    }
}
