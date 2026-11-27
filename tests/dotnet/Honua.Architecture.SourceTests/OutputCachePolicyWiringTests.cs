// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class OutputCachePolicyWiringTests
{
    [ArchitectureTest]
    public void MapServerQueryDomains_UsesDedicatedLayersVaryPolicy()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var mapServerEndpoints = File.ReadAllText(ArchitectureTestHelpers.CombinePath(
            repoRoot, "src", "Honua.Protocols", "GeoServices", "MapServer", "MapServerEndpoints.cs"));

        var routeIndex = mapServerEndpoints.IndexOf("MapGet(\"/rest/services/{serviceId}/MapServer/queryDomains\"", StringComparison.Ordinal);
        routeIndex.Should().BeGreaterThanOrEqualTo(0);

        var statementEnd = mapServerEndpoints.IndexOf(';', routeIndex);
        var routeRegistration = mapServerEndpoints[routeIndex..statementEnd];
        routeRegistration.Should().Contain(".CacheOutput(\"MapServerQueryDomains\")");
    }

    [ArchitectureTest]
    public void OgcTilesTilesets_UsesCollectionsVaryPolicy()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var tilesEndpoints = File.ReadAllText(ArchitectureTestHelpers.CombinePath(
            repoRoot, "src", "Honua.Protocols", "OgcApi", "Tiles", "TilesEndpoints.cs"));

        var routeIndex = tilesEndpoints.IndexOf("MapGet(\"/ogc/tiles/tiles\"", StringComparison.Ordinal);
        routeIndex.Should().BeGreaterThanOrEqualTo(0);

        var statementEnd = tilesEndpoints.IndexOf(';', routeIndex);
        var routeRegistration = tilesEndpoints[routeIndex..statementEnd];
        routeRegistration.Should().Contain(".CacheOutput(\"OgcTilesTilesets\")");

        var cacheRegistration = File.ReadAllText(ArchitectureTestHelpers.CombinePath(
            repoRoot, "src", "Honua.Hosting", "Features", "Caching", "OutputCachingRegistration.cs"));
        cacheRegistration.Should().Contain("AddPolicy(\"MapServerQueryDomains\"");
        cacheRegistration.Should().Contain("policy.SetVaryByQuery(\"f\", \"layers\")");
        cacheRegistration.Should().Contain("policy.SetVaryByQuery(\"f\", \"collections\")");
    }
}
