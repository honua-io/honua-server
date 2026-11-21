// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Exports;
using Honua.Server.Features.Admin.Exports;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

// Each companion file is compared with a reviewed golden copy, so any wording or format change is deliberate.
public sealed class ExportCompanionFilesTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [UnitTheory]
    [InlineData(ExportCompanionFiles.ReadmeFileName, "README.golden")]
    [InlineData(ExportCompanionFiles.AgentsFileName, "AGENTS.golden")]
    [InlineData(ExportCompanionFiles.StyleFileName, "style.golden.json")]
    public void Render_MatchesTheGoldenFile(string fileName, string golden)
    {
        var file = ExportCompanionFiles.Render(Input(StoredStyle)).Single(candidate => candidate.FileName == fileName);

        Encoding.UTF8.GetString(file.Content).ReplaceLineEndings("\n").Should().Be(Golden(golden));
    }

    [UnitTest]
    public void Render_HiddenField_IsNamedInNoCompanionFile()
    {
        var files = ExportCompanionFiles.Render(Input(StoredStyle));

        files.Should().OnlyContain(file => !Encoding.UTF8.GetString(file.Content).Contains("owner", StringComparison.OrdinalIgnoreCase));
        ExportVersionFactory.Collection(Input(null))["table:columns"]!.AsArray()
            .Select(column => column!["name"]!.GetValue<string>()).Should().Equal("objectid", "tmk");
    }

    [UnitTheory]
    [InlineData("{not json")]
    [InlineData("{\"version\":8,\"layers\":[{\"id\":\"labels\",\"type\":\"symbol\"}]}")]
    [InlineData(null)]
    public void StyleAsset_WithoutUsableStoredLayers_DrawsTheGeometryWithDefaults(string? stored)
    {
        var style = JsonNode.Parse(ExportStyleAsset.Render(Input(stored)))!;

        style["layers"]!.AsArray().Select(layer => layer!["type"]!.GetValue<string>()).Should().Equal("fill", "line");
        style["layers"]!.AsArray().Should().OnlyContain(layer =>
            layer!["source"]!.GetValue<string>() == ExportStyleAsset.SourceId && layer["source-layer"]!.GetValue<string>() == "layer");
    }

    [UnitTheory]
    [InlineData("[\"==\", \"owner\", \"State\"]")]
    [InlineData("[\"has\", \"owner\"]")]
    [InlineData("[\"match\", [\"get\", \"owner\"], \"State\", true, false]")]
    [InlineData("[\"has\", [\"concat\", \"own\", \"er\"]]")]
    [InlineData("[\"==\", [\"get\", \"$secret\"], 1]")]
    public void PublicStoredLayers_LayerReadingAHiddenField_IsDropped(string filter)
        => ExportStyleAsset.PublicStoredLayers(LineLayer(filter), ["objectid", "tmk"]).Should().BeNull();

    [UnitTheory]
    [InlineData("[\"==\", [\"get\", \"tmk\"], \"1-2-3\"]")]
    [InlineData("[\"==\", \"$type\", \"Polygon\"]")]
    public void PublicStoredLayers_LayerReadingPublicFields_IsKept(string filter)
        => ExportStyleAsset.PublicStoredLayers(LineLayer(filter), ["objectid", "tmk"]).Should().ContainSingle();

    private static string LineLayer(string filter)
        => $"[{{\"id\":\"a\",\"type\":\"line\",\"filter\":{filter},\"paint\":{{\"line-color\":\"#000000\"}}}}]";

    [UnitTheory]
    [InlineData("CC-BY-4.0", "CC-BY-4.0", "CC-BY-4.0")]
    [InlineData("proprietary", "other", "Proprietary")]
    [InlineData(null, null, "Not stated")]
    public void License_MapsMetadataToStac(string? license, string? stac, string label)
    {
        var metadata = new MetadataV2ObjectMetadata { License = license };

        ExportStacFacts.License(metadata).Should().Be(stac);
        ExportStacFacts.LicenseLabel(metadata).Should().Be(label);
    }

    [UnitTest]
    public void Collection_LinksCompanionsAndCarriesLicenceAndProviders()
    {
        var collection = ExportVersionFactory.Collection(Input(null));

        collection["links"]!.AsArray().Where(link => link!["type"]!.GetValue<string>() == "text/markdown")
            .Select(link => $"{link!["rel"]}={link["href"]}").Should().Equal("agents=AGENTS.md", "describedby=README.md");
        collection["assets"]!["style"]!["type"]!.GetValue<string>().Should().Be(ExportCompanionFiles.StyleMediaType);
        collection["assets"]!["thumbnail"].Should().BeNull("no thumbnail was rendered for this input");
        ExportVersionFactory.Collection(Input(null) with { Thumbnail = [1] })["assets"]!["thumbnail"]!["roles"]![0]!
            .GetValue<string>().Should().Be("thumbnail");
        collection["license"]!.GetValue<string>().Should().Be("CC-BY-4.0");
        collection["providers"]!.ToJsonString().Should().Be(
            "[{\"name\":\"City and County of Honolulu\",\"roles\":[\"producer\"],\"url\":\"https://example.gov/gis\"},"
            + "{\"name\":\"Example GIS\",\"roles\":[\"host\"],\"url\":\"https://example.gov/gis\"}]");
    }

    private const string StoredStyle = """
        {
          "version": 8,
          "sprite": "https://tiles.example/sprite",
          "glyphs": "https://tiles.example/{fontstack}/{range}.pbf",
          "sources": { "parcels": { "type": "vector", "tiles": ["https://tiles.example/{z}/{x}/{y}.pbf"] } },
          "layers": [
            { "id": "parcel-fill", "type": "fill", "source": "parcels", "source-layer": "parcels", "metadata": { "note": "owner" }, "paint": { "fill-color": "#88AA44" } },
            { "id": "by-owner", "type": "fill", "source": "parcels", "filter": ["==", ["get", "owner"], "State"], "paint": { "fill-color": "#000000" } },
            { "id": "parcel-labels", "type": "symbol", "source": "parcels", "source-layer": "parcels", "layout": { "text-field": "{tmk}" } }
          ]
        }
        """;

    private static string Golden(string name)
        => File.ReadAllText(RepositoryPaths.Resolve("tests", "dotnet", "Honua.Server.Tests", "Features", "Admin", "ExportCompanionGolden", name))
            .ReplaceLineEndings("\n");

    private static ExportCompanionInput Input(string? storedStyle)
        => new(Descriptor(), new ExportLayerFacts(new MetadataV2ObjectMetadata
        {
            Title = "Layer title the descriptor overrides",
            Description = "Parcels for <b>Honolulu</b>.\n\nUpdated *nightly*.",
            License = "CC-BY-4.0",
            Attribution = "City & County of Honolulu",
            Publisher = "City and County of Honolulu",
            ContactPoint = new MetadataV2ContactPoint { Url = "https://example.gov/gis" }
        }, "Example GIS", storedStyle));

    private static ExportVersion Descriptor() => new()
    {
        ExportId = "parcels",
        Version = "20261117T120000Z-abcdef12",
        Title = "County parcels",
        PublishedAt = CapturedAt,
        SourceLayer = new ExportSourceLayer("county", "parcels"),
        Capture = new ExportCapture("capture-1", CapturedAt, "rev-1", 1234),
        Bounds = [-158.3, 21.25, -157.6, 21.75],
        Crs = new ExportCrs("EPSG:3857", "OGC:CRS84"),
        Fields =
        [
            new ExportVersionField("objectid", "integer", Public: true),
            new ExportVersionField("tmk", "string", Public: true) { Title = "Tax map key", Description = "Parcel | key" },
            new ExportVersionField("owner", "string", Public: false)
        ],
        Identity = new ExportIdentity("tmk", "tmk", "tmk"),
        Geometry = new ExportGeometry("geometry", "wkb", ["MultiPolygon"]),
        Artifacts = Artifacts(),
        Validation = new ExportValidation(ExportValidation.Passed, CapturedAt, [])
    };

    private static ExportArtifacts Artifacts() => new(
        new PmtilesExportArtifact
        {
            Href = "parcels.pmtiles",
            Bytes = 10,
            Sha256 = "aa",
            CaptureId = "capture-1",
            Fields = ["objectid", "tmk"],
            VectorLayer = "layer",
            Minzoom = 0,
            Maxzoom = 14
        },
        new GeoparquetExportArtifact
        {
            Href = "parcels.parquet",
            Bytes = 20,
            Sha256 = "bb",
            CaptureId = "capture-1",
            Fields = ["objectid", "tmk"]
        });
}
