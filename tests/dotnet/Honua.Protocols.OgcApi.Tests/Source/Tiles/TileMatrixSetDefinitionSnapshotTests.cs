// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Tiles;
using Honua.Protocols.Ogc.Api.Tiles;
using Honua.Protocols.Ogc.Api.Tiles.Models;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Tiles;

/// <summary>
/// CITE byte-identical guard: the new registry/geometry-driven
/// <see cref="OgcTilesUtilities.BuildTileMatrixSetDefinition"/> must produce output identical to
/// the legacy hardcoded <c>BuildWebMercatorQuadDefinition</c> / <c>BuildWorldCrs84QuadDefinition</c>
/// paths so OGC API Tiles 16/16 conformance does not regress. A drift here means the gridset
/// generalization changed advertised metadata and the WMTS/Tiles CITE suites must be re-checked.
/// </summary>
public class TileMatrixSetDefinitionSnapshotTests
{
    private static readonly TileLimits Limits = new() { MinTileZoom = 0, MaxTileZoom = 18 };
    private static readonly TileMatrixSetRegistry Registry = new(new TileMatrixSetDefinitionOptions());

    private static string Serialize(TileMatrixSetDefinition definition)
        => JsonSerializer.Serialize(definition, OgcTilesJsonContext.Default.TileMatrixSetDefinition);

    private static TileMatrixSetDefinition RegistryDefinition(string id)
    {
        Registry.TryGet(id, out var entry).Should().BeTrue();
        Registry.TryGetGeometry(id, Limits.MaxTileZoom, out var geometry).Should().BeTrue();
        return OgcTilesUtilities.BuildTileMatrixSetDefinition(entry!, geometry!, Limits.MinTileZoom);
    }

    [Fact]
    public void BuildWebMercatorQuadDefinition_RegistryDriven_MatchesLegacy()
    {
        var legacy = OgcTilesUtilities.BuildWebMercatorQuadDefinition(Limits);
        var fromRegistry = RegistryDefinition("WebMercatorQuad");

        fromRegistry.Should().BeEquivalentTo(legacy, options => options.WithStrictOrdering());
        Serialize(fromRegistry).Should().Be(Serialize(legacy));
    }

    [Fact]
    public void BuildWorldCrs84QuadDefinition_RegistryDriven_MatchesLegacy()
    {
        var legacy = OgcTilesUtilities.BuildWorldCrs84QuadDefinition(Limits);
        var fromRegistry = RegistryDefinition("WorldCRS84Quad");

        fromRegistry.Should().BeEquivalentTo(legacy, options => options.WithStrictOrdering());
        Serialize(fromRegistry).Should().Be(Serialize(legacy));
    }

    [Fact]
    public void BuildWebMercatorQuadDefinition_RespectsMinZoom()
    {
        var limits = new TileLimits { MinTileZoom = 3, MaxTileZoom = 6 };
        var legacy = OgcTilesUtilities.BuildWebMercatorQuadDefinition(limits);

        Registry.TryGet("WebMercatorQuad", out var entry).Should().BeTrue();
        Registry.TryGetGeometry("WebMercatorQuad", limits.MaxTileZoom, out var geometry).Should().BeTrue();
        var fromRegistry = OgcTilesUtilities.BuildTileMatrixSetDefinition(entry!, geometry!, limits.MinTileZoom);

        fromRegistry.TileMatrices.Select(m => m.Id).Should().Equal("3", "4", "5", "6");
        fromRegistry.Should().BeEquivalentTo(legacy, options => options.WithStrictOrdering());
    }

    [Fact]
    public void BuildTileMatrixSetDefinition_CustomGrid_EmitsConfiguredMatricesAndNoWellKnownScaleSet()
    {
        var options = new TileMatrixSetDefinitionOptions
        {
            Custom =
            {
                new CustomTileMatrixSet
                {
                    Id = "DemoGrid",
                    Crs = "http://www.opengis.net/def/crs/EPSG/0/3857",
                    Uri = "https://example.com/tms/DemoGrid",
                    Title = "Demo Grid",
                    Srid = 3857,
                    TopLeftCorner = [-20037508.342789244, 20037508.342789244],
                    TileWidth = 512,
                    TileHeight = 512,
                    Levels =
                    [
                        new TileMatrixLevel { Id = 0, ScaleDenominator = 559082264.0287178, CellSize = 156543.03392804097, MatrixWidth = 1, MatrixHeight = 1 },
                        new TileMatrixLevel { Id = 1, ScaleDenominator = 279541132.0143589, CellSize = 78271.51696402048, MatrixWidth = 2, MatrixHeight = 2 }
                    ]
                }
            }
        };
        var registry = new TileMatrixSetRegistry(options);

        registry.TryGet("DemoGrid", out var entry).Should().BeTrue();
        registry.TryGetGeometry("DemoGrid", 99, out var geometry).Should().BeTrue();
        var definition = OgcTilesUtilities.BuildTileMatrixSetDefinition(entry!, geometry!);

        definition.Id.Should().Be("DemoGrid");
        definition.Title.Should().Be("Demo Grid");
        definition.Uri.Should().Be("https://example.com/tms/DemoGrid");
        definition.WellKnownScaleSet.Should().BeNull();
        definition.TileMatrices.Should().HaveCount(2);
        definition.TileMatrices[0].TileWidth.Should().Be(512);
        definition.TileMatrices[0].MatrixWidth.Should().Be(1);
        definition.TileMatrices[1].MatrixWidth.Should().Be(2);
        definition.TileMatrices.Select(m => m.Id).Should().Equal("0", "1");
    }
}
