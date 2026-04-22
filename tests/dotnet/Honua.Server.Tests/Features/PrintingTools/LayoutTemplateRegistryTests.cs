// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.PrintingTools;
using Honua.Server.Features.PrintingTools.Layout;
using Honua.Server.Features.PrintingTools.Models;
using Xunit;

namespace Honua.Server.Tests.Features.PrintingTools;

/// <summary>
/// Unit tests for layout template registry and URL resolution.
/// </summary>
public sealed class LayoutTemplateRegistryTests
{
    [Fact]
    public void GetTemplates_ReturnsAtLeastThreeTemplates()
    {
        var templates = LayoutTemplateRegistry.GetTemplates();

        // Acceptance criteria: at least 3 predefined templates
        templates.Count.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void GetTemplateNames_ReturnsExpectedBuiltInTemplates()
    {
        var names = LayoutTemplateRegistry.GetTemplateNames();

        names.Should().Contain("MAP_ONLY");
        names.Should().Contain("Letter ANSI A Portrait");
        names.Should().Contain("Letter ANSI A Landscape");
        names.Should().Contain("A4 Portrait");
        names.Should().Contain("A4 Landscape");
        names.Should().Contain("A3 Portrait");
        names.Should().Contain("A3 Landscape");
    }

    [Theory]
    [InlineData("MAP_ONLY")]
    [InlineData("Letter ANSI A Portrait")]
    [InlineData("Letter ANSI A Landscape")]
    [InlineData("A4 Portrait")]
    [InlineData("A4 Landscape")]
    [InlineData("A3 Portrait")]
    [InlineData("A3 Landscape")]
    public void TryGetTemplate_KnownName_ReturnsTemplate(string name)
    {
        var result = LayoutTemplateRegistry.TryGetTemplate(name, out var template);

        result.Should().BeTrue();
        template.Should().NotBeNull();
        template.Name.Should().Be(name);
        template.PageWidth.Should().BeGreaterThan(0);
        template.PageHeight.Should().BeGreaterThan(0);
        template.MapFrame.Should().NotBeNull();
        template.MapFrame.Width.Should().BeGreaterThan(0);
        template.MapFrame.Height.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("map_only")]
    [InlineData("MAP_ONLY")]
    [InlineData("Map_Only")]
    public void TryGetTemplate_CaseInsensitive_ReturnsTemplate(string name)
    {
        var result = LayoutTemplateRegistry.TryGetTemplate(name, out var template);

        result.Should().BeTrue();
        template.Should().NotBeNull();
    }

    [Fact]
    public void TryGetTemplate_NullOrEmpty_ReturnsMapOnly()
    {
        LayoutTemplateRegistry.TryGetTemplate(null, out var t1).Should().BeTrue();
        t1.Name.Should().Be("MAP_ONLY");

        LayoutTemplateRegistry.TryGetTemplate("", out var t2).Should().BeTrue();
        t2.Name.Should().Be("MAP_ONLY");
    }

    [Fact]
    public void TryGetTemplate_UnknownName_ReturnsFalse()
    {
        var result = LayoutTemplateRegistry.TryGetTemplate("NonExistent", out _);

        result.Should().BeFalse();
    }

    [Fact]
    public void MapOnly_IsMapOnlyTemplate()
    {
        LayoutTemplateRegistry.TryGetTemplate("MAP_ONLY", out var template);

        template!.IsMapOnly.Should().BeTrue();
        template.Title.Should().BeNull();
        template.Legend.Should().BeNull();
        template.ScaleBar.Should().BeNull();
        template.NorthArrow.Should().BeNull();
    }

    [Theory]
    [InlineData("Letter ANSI A Portrait")]
    [InlineData("Letter ANSI A Landscape")]
    [InlineData("A4 Portrait")]
    [InlineData("A4 Landscape")]
    [InlineData("A3 Portrait")]
    [InlineData("A3 Landscape")]
    public void FullLayoutTemplates_HaveAllElements(string name)
    {
        LayoutTemplateRegistry.TryGetTemplate(name, out var template);

        template!.IsMapOnly.Should().BeFalse();
        template.Title.Should().NotBeNull();
        template.Legend.Should().NotBeNull();
        template.ScaleBar.Should().NotBeNull();
        template.NorthArrow.Should().NotBeNull();
        template.Attribution.Should().NotBeNull();
    }

    [Theory]
    [InlineData("Letter ANSI A Portrait")]
    [InlineData("Letter ANSI A Landscape")]
    [InlineData("A4 Portrait")]
    [InlineData("A4 Landscape")]
    [InlineData("A3 Portrait")]
    [InlineData("A3 Landscape")]
    public void FullLayoutTemplates_SlotsDoNotOverlapPage(string name)
    {
        LayoutTemplateRegistry.TryGetTemplate(name, out var template);

        AssertSlotWithinPage(template!.MapFrame, template.PageWidth, template.PageHeight);
        if (template.Title is not null) AssertSlotWithinPage(template.Title, template.PageWidth, template.PageHeight);
        if (template.Legend is not null) AssertSlotWithinPage(template.Legend, template.PageWidth, template.PageHeight);
        if (template.ScaleBar is not null) AssertSlotWithinPage(template.ScaleBar, template.PageWidth, template.PageHeight);
        if (template.NorthArrow is not null) AssertSlotWithinPage(template.NorthArrow, template.PageWidth, template.PageHeight);
        if (template.Attribution is not null) AssertSlotWithinPage(template.Attribution, template.PageWidth, template.PageHeight);
    }

    [Fact]
    public void ResolveLayerFromUrl_ValidMapServerUrl_ExtractsServiceAndLayer()
    {
        var layer = new WebMapOperationalLayer
        {
            Url = "https://example.com/rest/services/myService/MapServer/3"
        };

        PrintingToolsRequestHandlers.ResolveLayerFromUrl(layer);

        layer.ResolvedServiceId.Should().Be("myService");
        layer.ResolvedLayerId.Should().Be(3);
    }

    [Fact]
    public void ResolveLayerFromUrl_ValidFeatureServerUrl_ExtractsServiceAndLayer()
    {
        var layer = new WebMapOperationalLayer
        {
            Url = "https://example.com/rest/services/testService/FeatureServer/0"
        };

        PrintingToolsRequestHandlers.ResolveLayerFromUrl(layer);

        layer.ResolvedServiceId.Should().Be("testService");
        layer.ResolvedLayerId.Should().Be(0);
    }

    [Fact]
    public void ResolveLayerFromUrl_ServiceOnlyUrl_ExtractsServiceWithNullLayerId()
    {
        var layer = new WebMapOperationalLayer
        {
            Url = "https://example.com/rest/services/myService/MapServer"
        };

        PrintingToolsRequestHandlers.ResolveLayerFromUrl(layer);

        layer.ResolvedServiceId.Should().Be("myService");
        layer.ResolvedLayerId.Should().BeNull();
    }

    [Fact]
    public void ResolveLayerFromUrl_InvalidUrl_DoesNotResolve()
    {
        var layer = new WebMapOperationalLayer
        {
            Url = "https://external-server.com/some/other/path"
        };

        PrintingToolsRequestHandlers.ResolveLayerFromUrl(layer);

        layer.ResolvedServiceId.Should().BeNull();
        layer.ResolvedLayerId.Should().BeNull();
    }

    [Fact]
    public void ParseWebMapJson_ValidJson_ReturnsDefinition()
    {
        var json = """{"mapOptions":{"extent":{"xmin":-122,"ymin":37,"xmax":-121,"ymax":38}}}""";
        var result = PrintingToolsRequestHandlers.ParseWebMapJson(json);

        result.Should().NotBeNull();
        result!.MapOptions.Should().NotBeNull();
        result.MapOptions!.Extent.Should().NotBeNull();
        result.MapOptions.Extent!.Xmin.Should().Be(-122);
    }

    [Fact]
    public void ParseWebMapJson_InvalidJson_ReturnsNull()
    {
        var result = PrintingToolsRequestHandlers.ParseWebMapJson("{invalid json");

        result.Should().BeNull();
    }

    [Fact]
    public void ParseWebMapJson_NullOrEmpty_ReturnsNull()
    {
        PrintingToolsRequestHandlers.ParseWebMapJson(null).Should().BeNull();
        PrintingToolsRequestHandlers.ParseWebMapJson("").Should().BeNull();
    }

    [Fact]
    public void ResolveExtentSrid_PrefersLatestWkidOverWkid()
    {
        // Esri alias 102645 → EPSG 2229 (NAD83 California zone 5 ftUS)
        var sr = new WebMapSpatialReference { Wkid = 102645, LatestWkid = 2229 };

        var srid = PrintingToolsRequestHandlers.ResolveExtentSrid(sr);

        srid.Should().Be(2229);
    }

    [Fact]
    public void ResolveExtentSrid_FallsBackToWkidWhenLatestWkidNull()
    {
        var sr = new WebMapSpatialReference { Wkid = 4326 };

        var srid = PrintingToolsRequestHandlers.ResolveExtentSrid(sr);

        srid.Should().Be(4326);
    }

    [Fact]
    public void ResolveExtentSrid_NullSpatialReference_DefaultsTo4326()
    {
        var srid = PrintingToolsRequestHandlers.ResolveExtentSrid(null);

        srid.Should().Be(4326);
    }

    [Fact]
    public void ValidateWebMapExtent_MissingExtent_ReturnsError()
    {
        var webMap = new WebMapDefinition { MapOptions = null };

        var error = PrintingToolsRequestHandlers.ValidateWebMapExtent(webMap);

        error.Should().NotBeNull();
        error.Should().Contain("extent");
    }

    [Fact]
    public void ValidateWebMapExtent_ValidExtentWithWkid_ReturnsNull()
    {
        var webMap = new WebMapDefinition
        {
            MapOptions = new WebMapExtent
            {
                Extent = new WebMapBbox
                {
                    Xmin = -122,
                    Ymin = 37,
                    Xmax = -121,
                    Ymax = 38,
                    SpatialReference = new WebMapSpatialReference { Wkid = 4326 }
                }
            }
        };

        var error = PrintingToolsRequestHandlers.ValidateWebMapExtent(webMap);

        error.Should().BeNull();
    }

    [Fact]
    public void ValidateWebMapExtent_WktOnlySpatialReference_ReturnsError()
    {
        var webMap = new WebMapDefinition
        {
            MapOptions = new WebMapExtent
            {
                Extent = new WebMapBbox
                {
                    Xmin = -122,
                    Ymin = 37,
                    Xmax = -121,
                    Ymax = 38,
                    SpatialReference = new WebMapSpatialReference { Wkt = "GEOGCS[\"GCS_WGS_1984\"]" }
                }
            }
        };

        var error = PrintingToolsRequestHandlers.ValidateWebMapExtent(webMap);

        error.Should().NotBeNull();
        error.Should().Contain("WKT");
    }

    [Fact]
    public void ValidateWebMapExtent_MapLevelWktOnlySpatialReference_ReturnsError()
    {
        // Extent has no SR, but map-level SR has only WKT — should be rejected
        // to prevent silent fallback to EPSG:4326
        var webMap = new WebMapDefinition
        {
            MapOptions = new WebMapExtent
            {
                Extent = new WebMapBbox { Xmin = -122, Ymin = 37, Xmax = -121, Ymax = 38 },
                SpatialReference = new WebMapSpatialReference { Wkt = "PROJCS[\"NAD83_California_zone_5\"]" }
            }
        };

        var error = PrintingToolsRequestHandlers.ValidateWebMapExtent(webMap);

        error.Should().NotBeNull();
        error.Should().Contain("WKT");
    }

    [Fact]
    public void ValidateWebMapExtent_MapLevelWkidSpatialReference_ReturnsNull()
    {
        // Map-level SR with WKID is valid (used as fallback for extent)
        var webMap = new WebMapDefinition
        {
            MapOptions = new WebMapExtent
            {
                Extent = new WebMapBbox { Xmin = -122, Ymin = 37, Xmax = -121, Ymax = 38 },
                SpatialReference = new WebMapSpatialReference { Wkid = 3857 }
            }
        };

        var error = PrintingToolsRequestHandlers.ValidateWebMapExtent(webMap);

        error.Should().BeNull();
    }

    [Fact]
    public void ValidateWebMapExtent_NoSpatialReference_ReturnsNull()
    {
        // No spatialReference at all defaults to 4326 — that's acceptable
        var webMap = new WebMapDefinition
        {
            MapOptions = new WebMapExtent
            {
                Extent = new WebMapBbox { Xmin = -122, Ymin = 37, Xmax = -121, Ymax = 38 }
            }
        };

        var error = PrintingToolsRequestHandlers.ValidateWebMapExtent(webMap);

        error.Should().BeNull();
    }

    [Fact]
    public void ResolveExtentSrid_FallsBackToMapLevelSr()
    {
        // Extent has no SR, but map-level SR provides the WKID
        var mapSr = new WebMapSpatialReference { Wkid = 3857 };

        var srid = PrintingToolsRequestHandlers.ResolveExtentSrid(null, mapSr);

        srid.Should().Be(3857);
    }

    [Fact]
    public void ResolveExtentSrid_ExtentSrTakesPrecedenceOverMapLevel()
    {
        var extentSr = new WebMapSpatialReference { Wkid = 4326 };
        var mapSr = new WebMapSpatialReference { Wkid = 3857 };

        var srid = PrintingToolsRequestHandlers.ResolveExtentSrid(extentSr, mapSr);

        srid.Should().Be(4326);
    }

    [Fact]
    public void ResolveExtentSrid_MapLevelLatestWkidPreferred()
    {
        var mapSr = new WebMapSpatialReference { Wkid = 102100, LatestWkid = 3857 };

        var srid = PrintingToolsRequestHandlers.ResolveExtentSrid(null, mapSr);

        srid.Should().Be(3857);
    }

    [Fact]
    public void ValidateMapOnlyOutputSize_ExceedsMaxDimension_ReturnsError()
    {
        var webMap = new WebMapDefinition
        {
            MapOptions = new WebMapExtent
            {
                Extent = new WebMapBbox { Xmin = -122, Ymin = 37, Xmax = -121, Ymax = 38 }
            },
            ExportOptions = new WebMapExportOptions { OutputSize = [10000, 10000] }
        };

        var error = PrintingToolsRequestHandlers.ValidateMapOnlyOutputSize(webMap, "MAP_ONLY");

        error.Should().NotBeNull();
        error.Should().Contain("4096");
    }

    [Fact]
    public void ValidateMapOnlyOutputSize_WithinMax_ReturnsNull()
    {
        var webMap = new WebMapDefinition
        {
            MapOptions = new WebMapExtent
            {
                Extent = new WebMapBbox { Xmin = -122, Ymin = 37, Xmax = -121, Ymax = 38 }
            },
            ExportOptions = new WebMapExportOptions { OutputSize = [4096, 4096] }
        };

        var error = PrintingToolsRequestHandlers.ValidateMapOnlyOutputSize(webMap, "MAP_ONLY");

        error.Should().BeNull();
    }

    [Fact]
    public void ValidateMapOnlyOutputSize_SingleElementExceedsMax_ReturnsError()
    {
        var webMap = new WebMapDefinition
        {
            MapOptions = new WebMapExtent
            {
                Extent = new WebMapBbox { Xmin = -122, Ymin = 37, Xmax = -121, Ymax = 38 }
            },
            ExportOptions = new WebMapExportOptions { OutputSize = [5000] }
        };

        var error = PrintingToolsRequestHandlers.ValidateMapOnlyOutputSize(webMap, "MAP_ONLY");

        error.Should().NotBeNull();
        error.Should().Contain("4096");
    }

    private static void AssertSlotWithinPage(LayoutSlot slot, float pageWidth, float pageHeight)
    {
        slot.X.Should().BeGreaterThanOrEqualTo(0);
        slot.Y.Should().BeGreaterThanOrEqualTo(0);
        (slot.X + slot.Width).Should().BeLessThanOrEqualTo(pageWidth + 1f);
        (slot.Y + slot.Height).Should().BeLessThanOrEqualTo(pageHeight + 1f);
    }
}
