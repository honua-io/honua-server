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

    [ArchitectureTest]
    public void ProtocolReference_DescribesNaServerSolversAndGpContextContract()
    {
        // #4037: the protocol reference must describe every served NAServer solver with the
        // HTTP methods actually registered, and keep the VersionManagementServer
        // 404-by-default caveat. Only Route/solve has a GET route; if another solver gains
        // one, this fails so the docs are updated with it. #4030/#4034: the GP guide and
        // parity judgment must state how `context` and failed sync runs behave.
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var protocol = File.ReadAllText(ArchitectureTestHelpers.CombinePath(root, "docs", "reference", "protocols", "geoservices-rest.md"));
        var parity = File.ReadAllText(ArchitectureTestHelpers.CombinePath(root, "docs", "reference", "compatibility", "geoservices-parity.md"));
        var guide = File.ReadAllText(ArchitectureTestHelpers.CombinePath(root, "docs", "guides", "query-analyze", "run-geoprocessing.md"));
        var judgment = File.ReadAllText(ArchitectureTestHelpers.CombinePath(root, "docs", "gis", "data", "geoservices-parity-judgment.json"));

        var naServerSolveRoutes = Honua.Server.EndpointRegistry.All
            .Where(endpoint => endpoint.Path.Contains("/NAServer/", StringComparison.Ordinal)
                && endpoint.Path.Contains("/solve", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        naServerSolveRoutes.Where(endpoint => endpoint.Method == "POST").Select(endpoint => endpoint.Path)
            .Should().BeEquivalentTo(
                "/rest/services/{serviceId}/NAServer/Route/solve",
                "/rest/services/{serviceId}/NAServer/ServiceArea/solveServiceArea",
                "/rest/services/{serviceId}/NAServer/ClosestFacility/solveClosestFacility",
                "/rest/services/{serviceId}/NAServer/ODCostMatrix/solveODCostMatrix",
                "/rest/services/{serviceId}/NAServer/LocationAllocation/solveLocationAllocation");
        naServerSolveRoutes.Where(endpoint => endpoint.Method == "GET").Select(endpoint => endpoint.Path)
            .Should().Equal(["/rest/services/{serviceId}/NAServer/Route/solve"],
                "the NAServer docs say only Route/solve is served over GET");

        protocol.Should().Contain(
            "Route solves are available over GET and POST; ServiceArea, ClosestFacility, ODCostMatrix, and LocationAllocation solves are POST-only");
        protocol.Should().NotContain("GET and POST solves are available for Route, ServiceArea");
        parity.Should().Contain("`Route/solve` accepts GET query parameters or POST form parameters");
        judgment.Should().NotContain("All five NAServer solve operations accept GET");
        protocol.Should().Contain("Experimental and off by default; routes return 404 until `versioning.branch` is enabled.");
        guide.Should().Contain("`context.outSR` and `context.processSR` behave exactly like `env:outSR` and `env:processSR`");
        guide.Should().Contain("returns the GeoServices error envelope");
        judgment.Should().Contain("context.outSR/context.processSR are applied as their env:* equivalents (#4030)");
        judgment.Should().Contain("instead of an empty results list (#4034)");
    }

    [ArchitectureTest]
    public void ParityData_LayerApplyEditsControls_MatchTheServedContract()
    {
        // #4105: the parity data once called every Esri applyEdits control "ignored" while the server
        // answered 400 for them. Each control's disposition is pinned here and proven on the wire by
        // FeatureServerEndpointTests: ApplyEdits_NoOpEsriClientControl_IsAcceptedAndEditApplies (ignored),
        // ApplyEdits_UnsupportedEsriControl_IsRejectedInsteadOfDropped (rejected), and
        // ApplyEdits_ReturnEditMomentTrue_ReportsWhenEditsWereApplied (returnEditMoment implemented).
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["adds"] = "implemented",
            ["updates"] = "implemented",
            ["deletes"] = "implemented",
            ["rollbackOnFailure"] = "implemented",
            ["returnEditMoment"] = "implemented",
            ["useGlobalIds"] = "rejected",
            ["gdbVersion"] = "rejected",
            ["sessionID"] = "ignored",
            ["trueCurveClient"] = "ignored",
            ["usePreviousEditMoment"] = "ignored",
            ["timeReferenceUnknownClient"] = "ignored",
            ["returnEditResults"] = "ignored",
            ["attachments"] = "rejected",
            ["assetMaps"] = "rejected",
            ["async"] = "rejected",
            ["useUniqueIds"] = "rejected",
            ["editsUploadId"] = "rejected",
            ["editsUploadFormat"] = "rejected",
            ["datumTransformation"] = "rejected",
        };

        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        foreach (var file in new[] { "geoservices-parity-judgment.json", "geoservices-rest-parity.json" })
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(ArchitectureTestHelpers.CombinePath(root, "docs", "gis", "data", file)));
            var rows = FindProperty(document.RootElement, "layerApplyEdits");
            rows.Should().NotBeNull($"{file} must classify the layer applyEdits parameters");

            var actual = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in rows!.Value.EnumerateArray())
            {
                var status = row.GetProperty("status").GetString()!;
                foreach (var name in row.GetProperty("name").GetString()!.Split(',', StringSplitOptions.TrimEntries))
                {
                    actual.Add(name, status);
                }
            }

            actual.Should().BeEquivalentTo(expected, $"{file} must describe the applyEdits controls the server serves");
        }
    }

    private static System.Text.Json.JsonElement? FindProperty(System.Text.Json.JsonElement element, string name)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals(name))
                    {
                        return property.Value;
                    }

                    if (FindProperty(property.Value, name) is { } nested)
                    {
                        return nested;
                    }
                }

                break;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindProperty(item, name) is { } nested)
                    {
                        return nested;
                    }
                }

                break;
        }

        return null;
    }
}
