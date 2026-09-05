// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;

namespace Honua.Architecture.Tests;

/// <summary>Guards customer-facing GeoServices claims that must match the wire contract.</summary>
public sealed class GeoServicesDocumentationTruthTests
{
    [ArchitectureTest]
    public void ParityDocs_DoNotAdvertiseRejectedExportParameters()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var parity = File.ReadAllText(ArchitectureTestHelpers.CombinePath(root, "docs", "reference", "compatibility", "geoservices-parity.md"));
        var judgment = File.ReadAllText(ArchitectureTestHelpers.CombinePath(root, "docs", "gis", "data", "geoservices-parity-judgment.json"));

        parity.Should().Contain("png/png8/png24/png32/jpg/jpeg").And.NotContain("png/png8/png24/png32/jpg/gif");
        parity.Should().Contain("Explicit `noData` overrides and non-`UNKNOWN` `pixelType` values return 501");
        judgment.Should().Contain("every explicit conversion type returns 501");
        judgment.Should().Contain("\"name\": \"noData, noDataInterpretation\"");
        judgment.Should().NotContain("\"name\": \"bandIds, noData, noDataInterpretation\"");
    }

    [ArchitectureTest]
    public void GeometryAndCatalogClaims_MatchDescriptorsAndKnownCaveats()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var parity = File.ReadAllText(ArchitectureTestHelpers.CombinePath(root, "docs", "reference", "compatibility", "geoservices-parity.md"));
        var judgment = File.ReadAllText(ArchitectureTestHelpers.CombinePath(root, "docs", "gis", "data", "geoservices-parity-judgment.json"));

        parity.Should().Contain("it intentionally omits `currentVersion`");
        parity.Should().Contain("Known parameter-level caveats:").And.NotContain("caveats (the complete list)");
        parity.Should().Contain("`trimExtend.extendHow`").And.Contain("`offset.simplifyResult`");
        parity.Should().Contain("Only `/rest/info` advertises the compatibility value `currentVersion: 10.8`");
        judgment.Should().Contain("currentVersion is intentionally omitted");
        judgment.Should().Contain("trimExtend extendHow, offset simplifyResult");
    }

    [ArchitectureTest]
    public void MigrationGuide_DescribesImplementedAndGatedSurfaces()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var protocol = File.ReadAllText(ArchitectureTestHelpers.CombinePath(root, "docs", "reference", "protocols", "geoservices-rest.md"));
        var migration = File.ReadAllText(ArchitectureTestHelpers.CombinePath(root, "docs", "guides", "migrate", "arcgis-apps-and-sdks.md"));

        protocol.Should().Contain("`queryContingentValues` is implemented");
        protocol.Should().Contain("experimental capability `serve.i3s-scene`");
        protocol.Should().Contain("routes return 404 until `versioning.branch` is enabled");
        migration.Should().Contain("MapServer WMTS supports WebMercatorQuad, WorldCRS84Quad");
        migration.Should().Contain("Incremental Postgres change tracking");
        var pro = File.ReadAllText(ArchitectureTestHelpers.CombinePath(root, "docs", "guides", "connect", "arcgis-pro.md"));
        pro.Should().Contain("run the `PortalCompat.generateToken` example").And.NotContain("command above");
        pro.Should().Contain("default is 404 until experimental capability `serve.i3s-scene` is enabled");
    }
}
