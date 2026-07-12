// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Tiles;
using Honua.Protocols.Ogc.Classic.Wmts;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wmts;

/// <summary>
/// Unit coverage for the operator-defined custom tile matrix set emission in WMTS
/// GetCapabilities. The built-in WebMercatorQuad / WorldCRS84Quad XML is emitted by separate,
/// untouched methods so it stays byte-identical (the CITE WMTS 60/60 guard); these tests pin the
/// shape of the additive custom-grid XML, including the CRS-keyed TopLeftCorner axis order
/// (CRS84 URI -> lon,lat; EPSG geographic URN -> lat,lon; projected -> easting,northing).
/// </summary>
public class WmtsCustomTileMatrixSetXmlTests
{
    private static TileMatrixSetRegistry RegistryWith(CustomTileMatrixSet custom)
        => new(new TileMatrixSetDefinitionOptions { Custom = { custom } });

    private static CustomTileMatrixSet ProjectedGrid() => new()
    {
        Id = "DemoProjected",
        Crs = "urn:ogc:def:crs:EPSG:6.18:3:3857",
        Srid = 3857,
        TopLeftCorner = [-20037508.342789244, 20037508.342789244],
        TileWidth = 256,
        TileHeight = 256,
        Levels =
        [
            new TileMatrixLevel { Id = 0, ScaleDenominator = 559082264.0287178, CellSize = 156543.03392804097, MatrixWidth = 1, MatrixHeight = 1 }
        ]
    };

    private static CustomTileMatrixSet GeographicGrid() => new()
    {
        Id = "DemoGeographic",
        Crs = "urn:ogc:def:crs:OGC:1.3:CRS84",
        Srid = 4326,
        TopLeftCorner = [-180.0, 90.0],
        TileWidth = 256,
        TileHeight = 256,
        Levels =
        [
            new TileMatrixLevel { Id = 0, ScaleDenominator = 279541132.0143589, CellSize = 0.703125, MatrixWidth = 2, MatrixHeight = 1 }
        ]
    };

    private static CustomTileMatrixSet EpsgGeographicGrid() => new()
    {
        Id = "DemoEpsgGeographic",
        Crs = "urn:ogc:def:crs:EPSG::4326",
        Srid = 4326,
        TopLeftCorner = [-180.0, 90.0],
        TileWidth = 256,
        TileHeight = 256,
        Levels =
        [
            new TileMatrixLevel { Id = 0, ScaleDenominator = 279541132.0143589, CellSize = 0.703125, MatrixWidth = 2, MatrixHeight = 1 }
        ]
    };

    [Fact]
    public void AppendCustomTileMatrixSets_NullRegistry_EmitsNothing()
    {
        var sb = new StringBuilder();
        WmtsRequestHandlers.AppendWmtsCustomTileMatrixSets(sb, null, 18);
        sb.ToString().Should().BeEmpty();
    }

    [Fact]
    public void AppendCustomTileMatrixSets_OnlyBuiltIns_EmitsNothing()
    {
        var sb = new StringBuilder();
        WmtsRequestHandlers.AppendWmtsCustomTileMatrixSets(sb, new TileMatrixSetRegistry(new TileMatrixSetDefinitionOptions()), 18);
        sb.ToString().Should().BeEmpty();
    }

    [Fact]
    public void AppendCustomTileMatrixSets_ProjectedGrid_EmitsXyTopLeftCorner()
    {
        var sb = new StringBuilder();
        WmtsRequestHandlers.AppendWmtsCustomTileMatrixSets(sb, RegistryWith(ProjectedGrid()), 18);
        var xml = sb.ToString();

        xml.Should().Contain("<ows:Identifier>DemoProjected</ows:Identifier>");
        xml.Should().Contain("<ows:SupportedCRS>urn:ogc:def:crs:EPSG:6.18:3:3857</ows:SupportedCRS>");
        // Projected: easting (negative) then northing (positive). Assert the easting-first prefix
        // rather than the full round-trip tail so the axis-order check is robust to double
        // formatting precision.
        xml.Should().Contain("<TopLeftCorner>-20037508.342789");
        xml.Should().MatchRegex(@"<TopLeftCorner>-20037508\.342789\d* 20037508\.342789\d*</TopLeftCorner>");
        xml.Should().Contain("<MatrixWidth>1</MatrixWidth>");
    }

    [Fact]
    public void AppendCustomTileMatrixSets_Crs84Grid_EmitsLonLatTopLeftCorner()
    {
        var sb = new StringBuilder();
        WmtsRequestHandlers.AppendWmtsCustomTileMatrixSets(sb, RegistryWith(GeographicGrid()), 18);
        var xml = sb.ToString();

        xml.Should().Contain("<ows:Identifier>DemoGeographic</ows:Identifier>");
        // CRS84 is a lon,lat CRS: longitude (-180) then latitude (90), per the OGC definition.
        xml.Should().Contain("<TopLeftCorner>-180 90</TopLeftCorner>");
        xml.Should().Contain("<MatrixWidth>2</MatrixWidth>");
        xml.Should().Contain("<MatrixHeight>1</MatrixHeight>");
    }

    [Fact]
    public void AppendCustomTileMatrixSets_EpsgGeographicGrid_EmitsLatLonTopLeftCorner()
    {
        var sb = new StringBuilder();
        WmtsRequestHandlers.AppendWmtsCustomTileMatrixSets(sb, RegistryWith(EpsgGeographicGrid()), 18);
        var xml = sb.ToString();

        xml.Should().Contain("<ows:Identifier>DemoEpsgGeographic</ows:Identifier>");
        // EPSG-authority geographic CRS (EPSG:4326) uses lat,lon: latitude (90) then longitude (-180).
        xml.Should().Contain("<TopLeftCorner>90 -180</TopLeftCorner>");
    }

    [Fact]
    public void AppendCustomTileMatrixSetLinks_EmitsLimitsForEachLevel()
    {
        var sb = new StringBuilder();
        WmtsRequestHandlers.AppendWmtsCustomTileMatrixSetLinks(sb, RegistryWith(GeographicGrid()), 18);
        var xml = sb.ToString();

        xml.Should().Contain("<TileMatrixSet>DemoGeographic</TileMatrixSet>");
        xml.Should().Contain("<MaxTileCol>1</MaxTileCol>");
        xml.Should().Contain("<MaxTileRow>0</MaxTileRow>");
    }
}
