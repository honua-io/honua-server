// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.PrintingTools;
using Honua.Server.Features.PrintingTools.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.PrintingTools;

/// <summary>
/// Unit tests for <see cref="PrintingToolsRequestHandlers"/> static helper methods.
/// </summary>
[Trait("Component", "PrintingTools")]
public class PrintingToolsRequestHandlerTests
{
    // --- ResolveLayerFromUrl ---

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ResolveLayerFromUrl_LayerLevelUrl_ResolvesServiceAndLayerId()
    {
        var layer = new WebMapOperationalLayer
        {
            Url = "https://example.com/rest/services/MyService/MapServer/3"
        };

        PrintingToolsRequestHandlers.ResolveLayerFromUrl(layer);

        layer.ResolvedServiceId.Should().Be("MyService");
        layer.ResolvedLayerId.Should().Be(3);
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ResolveLayerFromUrl_ServiceLevelUrl_ResolvesServiceOnly()
    {
        var layer = new WebMapOperationalLayer
        {
            Url = "https://example.com/rest/services/MyService/MapServer"
        };

        PrintingToolsRequestHandlers.ResolveLayerFromUrl(layer);

        layer.ResolvedServiceId.Should().Be("MyService");
        layer.ResolvedLayerId.Should().BeNull();
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ResolveLayerFromUrl_FeatureServerUrl_ResolvesCorrectly()
    {
        var layer = new WebMapOperationalLayer
        {
            Url = "https://example.com/rest/services/Parks/FeatureServer/0"
        };

        PrintingToolsRequestHandlers.ResolveLayerFromUrl(layer);

        layer.ResolvedServiceId.Should().Be("Parks");
        layer.ResolvedLayerId.Should().Be(0);
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ResolveLayerFromUrl_InvalidUrl_DoesNotResolve()
    {
        var layer = new WebMapOperationalLayer
        {
            Url = "https://example.com/some/other/path"
        };

        PrintingToolsRequestHandlers.ResolveLayerFromUrl(layer);

        layer.ResolvedServiceId.Should().BeNull();
        layer.ResolvedLayerId.Should().BeNull();
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ResolveLayerFromUrl_NullUrl_DoesNotResolve()
    {
        var layer = new WebMapOperationalLayer();

        PrintingToolsRequestHandlers.ResolveLayerFromUrl(layer);

        layer.ResolvedServiceId.Should().BeNull();
    }

    // --- ParseWebMapJson ---

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ParseWebMapJson_ValidJson_ReturnsDefinition()
    {
        var json = """{"mapOptions":{"extent":{"xmin":-180,"ymin":-90,"xmax":180,"ymax":90}}}""";

        var result = PrintingToolsRequestHandlers.ParseWebMapJson(json);

        result.Should().NotBeNull();
        result!.MapOptions.Should().NotBeNull();
        result.MapOptions!.Extent!.Xmin.Should().Be(-180);
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ParseWebMapJson_InvalidJson_ReturnsNull()
    {
        var result = PrintingToolsRequestHandlers.ParseWebMapJson("{invalid");

        result.Should().BeNull();
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ParseWebMapJson_Null_ReturnsNull()
    {
        PrintingToolsRequestHandlers.ParseWebMapJson(null).Should().BeNull();
        PrintingToolsRequestHandlers.ParseWebMapJson("").Should().BeNull();
        PrintingToolsRequestHandlers.ParseWebMapJson("  ").Should().BeNull();
    }

    // --- ResolveFormat ---

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ResolveFormat_Null_DefaultsToPng32()
    {
        PrintingToolsRequestHandlers.ResolveFormat(null).Should().Be("PNG32");
        PrintingToolsRequestHandlers.ResolveFormat("").Should().Be("PNG32");
        PrintingToolsRequestHandlers.ResolveFormat("  ").Should().Be("PNG32");
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ResolveFormat_ValidFormat_PreservedTrimmed()
    {
        PrintingToolsRequestHandlers.ResolveFormat("PDF").Should().Be("PDF");
        PrintingToolsRequestHandlers.ResolveFormat(" JPG ").Should().Be("JPG");
    }

    // --- ResolveDpi ---

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ResolveDpi_Default_Returns96()
    {
        var webMap = new WebMapDefinition();

        PrintingToolsRequestHandlers.ResolveDpi(webMap).Should().Be(96);
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ResolveDpi_Null_Returns96()
    {
        PrintingToolsRequestHandlers.ResolveDpi(null).Should().Be(96);
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ResolveDpi_ClampsToRange()
    {
        var low = new WebMapDefinition { ExportOptions = new WebMapExportOptions { Dpi = 10 } };
        PrintingToolsRequestHandlers.ResolveDpi(low).Should().Be(72);

        var high = new WebMapDefinition { ExportOptions = new WebMapExportOptions { Dpi = 9999 } };
        PrintingToolsRequestHandlers.ResolveDpi(high).Should().Be(600);
    }

    // --- ResolveExtentSrid ---

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ResolveExtentSrid_LatestWkid_TakesPrecedence()
    {
        var sr = new WebMapSpatialReference { Wkid = 102100, LatestWkid = 3857 };

        PrintingToolsRequestHandlers.ResolveExtentSrid(sr).Should().Be(3857);
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ResolveExtentSrid_WkidOnly_ReturnsWkid()
    {
        var sr = new WebMapSpatialReference { Wkid = 4326 };

        PrintingToolsRequestHandlers.ResolveExtentSrid(sr).Should().Be(4326);
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ResolveExtentSrid_Null_FallsToMapLevel()
    {
        var mapSr = new WebMapSpatialReference { Wkid = 3857 };

        PrintingToolsRequestHandlers.ResolveExtentSrid(null, mapSr).Should().Be(3857);
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ResolveExtentSrid_BothNull_Defaults4326()
    {
        PrintingToolsRequestHandlers.ResolveExtentSrid(null, null).Should().Be(4326);
    }

    // --- ValidateWebMapExtent ---

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ValidateWebMapExtent_ValidExtent_ReturnsNull()
    {
        var webMap = new WebMapDefinition
        {
            MapOptions = new WebMapOptions
            {
                Extent = new WebMapBbox { Xmin = -180, Ymin = -90, Xmax = 180, Ymax = 90 }
            }
        };

        PrintingToolsRequestHandlers.ValidateWebMapExtent(webMap).Should().BeNull();
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ValidateWebMapExtent_MissingExtent_ReturnsError()
    {
        var webMap = new WebMapDefinition { MapOptions = new WebMapOptions() };

        PrintingToolsRequestHandlers.ValidateWebMapExtent(webMap).Should().NotBeNull();
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ValidateWebMapExtent_WktOnly_ReturnsError()
    {
        var webMap = new WebMapDefinition
        {
            MapOptions = new WebMapOptions
            {
                Extent = new WebMapBbox
                {
                    Xmin = 0,
                    Ymin = 0,
                    Xmax = 1,
                    Ymax = 1,
                    SpatialReference = new WebMapSpatialReference { Wkt = "GEOGCS[\"GCS_WGS_1984\"]" }
                }
            }
        };

        var error = PrintingToolsRequestHandlers.ValidateWebMapExtent(webMap);
        error.Should().Contain("WKT");
    }

    // --- ValidateMapOnlyOutputSize ---

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ValidateMapOnlyOutputSize_WithSize_ReturnsNull()
    {
        var webMap = new WebMapDefinition
        {
            ExportOptions = new WebMapExportOptions { OutputSize = [800, 600] }
        };

        PrintingToolsRequestHandlers.ValidateMapOnlyOutputSize(webMap, "MAP_ONLY").Should().BeNull();
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ValidateMapOnlyOutputSize_MapOnlyWithoutSize_ReturnsError()
    {
        var webMap = new WebMapDefinition();

        PrintingToolsRequestHandlers.ValidateMapOnlyOutputSize(webMap, "MAP_ONLY").Should().NotBeNull();
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ValidateMapOnlyOutputSize_NonMapOnly_ReturnsNull()
    {
        var webMap = new WebMapDefinition();

        PrintingToolsRequestHandlers.ValidateMapOnlyOutputSize(webMap, "Letter_Portrait").Should().BeNull();
    }

    // --- ValidateEdition ---

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ValidateEdition_CommunityMapOnlyPng_Allowed()
    {
        var provider = CreateLicenseProvider(HonuaEdition.Community);

        PrintingToolsRequestHandlers.ValidateEdition("MAP_ONLY", "PNG32", provider).Should().BeNull();
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ValidateEdition_CommunityPdf_Blocked()
    {
        var provider = CreateLicenseProvider(HonuaEdition.Community);

        PrintingToolsRequestHandlers.ValidateEdition("MAP_ONLY", "PDF", provider).Should().Contain("Pro");
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ValidateEdition_CommunityLayoutTemplate_Blocked()
    {
        var provider = CreateLicenseProvider(HonuaEdition.Community);

        PrintingToolsRequestHandlers.ValidateEdition("Letter_Portrait", "PNG32", provider).Should().Contain("Pro");
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ValidateEdition_ProEdition_AllAllowed()
    {
        var provider = CreateLicenseProvider(HonuaEdition.Pro);

        PrintingToolsRequestHandlers.ValidateEdition("Letter_Portrait", "PDF", provider).Should().BeNull();
    }

    // --- CollectWarnings ---

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void CollectWarnings_WithBaseMap_EmitsWarning()
    {
        var json = """{"baseMap":{"title":"Topographic"},"mapOptions":{"extent":{"xmin":0,"ymin":0,"xmax":1,"ymax":1}}}""";
        var webMap = PrintingToolsRequestHandlers.ParseWebMapJson(json)!;
        var logger = NullLogger.Instance;

        var warnings = PrintingToolsRequestHandlers.CollectWarnings(webMap, logger);

        warnings.Should().ContainSingle();
        warnings[0].Type.Should().Be("esriJobMessageTypeWarning");
        warnings[0].Description.Should().Contain("baseMap");
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void CollectWarnings_WithoutBaseMap_NoWarnings()
    {
        var json = """{"mapOptions":{"extent":{"xmin":0,"ymin":0,"xmax":1,"ymax":1}}}""";
        var webMap = PrintingToolsRequestHandlers.ParseWebMapJson(json)!;
        var logger = NullLogger.Instance;

        var warnings = PrintingToolsRequestHandlers.CollectWarnings(webMap, logger);

        warnings.Should().BeEmpty();
    }

    // --- PrintOutputFormat ---

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void PrintOutputFormat_IsSupported_KnownFormats()
    {
        PrintOutputFormat.IsSupported("PDF").Should().BeTrue();
        PrintOutputFormat.IsSupported("PNG32").Should().BeTrue();
        PrintOutputFormat.IsSupported("JPG").Should().BeTrue();
        PrintOutputFormat.IsSupported("PNG8").Should().BeTrue();
        PrintOutputFormat.IsSupported("pdf").Should().BeTrue();
        PrintOutputFormat.IsSupported("TIFF").Should().BeFalse();
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void PrintOutputFormat_GetContentType_ReturnsCorrectMimeTypes()
    {
        PrintOutputFormat.GetContentType("PDF").Should().Be("application/pdf");
        PrintOutputFormat.GetContentType("PNG32").Should().Be("image/png");
        PrintOutputFormat.GetContentType("JPG").Should().Be("image/jpeg");
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void PrintOutputFormat_GetExtension_ReturnsCorrectExtensions()
    {
        PrintOutputFormat.GetExtension("PDF").Should().Be(".pdf");
        PrintOutputFormat.GetExtension("PNG32").Should().Be(".png");
        PrintOutputFormat.GetExtension("JPG").Should().Be(".jpg");
    }

    // --- Helpers ---

    private static ILicenseStatusProvider CreateLicenseProvider(HonuaEdition edition)
    {
        var provider = Substitute.For<ILicenseStatusProvider>();
        provider.GetCurrentStatus().Returns(new LicenseStatus(edition, true, null, null));
        return provider;
    }
}
