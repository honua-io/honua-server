// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.PrintingTools;
using Honua.Server.Features.PrintingTools.Layout;
using Honua.Server.Features.PrintingTools.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.PrintingTools;

/// <summary>
/// Unit tests for <see cref="PrintingToolsRequestHandlers"/> helpers not covered by
/// <see cref="LayoutTemplateRegistryTests"/> (edition gating, warnings, format/DPI resolution,
/// and output-format constants).
/// </summary>
[Trait("Component", "PrintingTools")]
public class PrintingToolsRequestHandlerTests
{
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

    // --- ValidateEdition ---

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ValidateEdition_CommunityMapOnlyPng_Allowed()
    {
        LayoutTemplateRegistry.TryGetTemplate("MAP_ONLY", out var template);

        PrintingToolsRequestHandlers.ValidateEdition(template, "PNG32", HonuaEdition.Community, NullLogger.Instance).Should().BeNull();
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ValidateEdition_CommunityPdf_Blocked()
    {
        LayoutTemplateRegistry.TryGetTemplate("MAP_ONLY", out var template);

        PrintingToolsRequestHandlers.ValidateEdition(template, "PDF", HonuaEdition.Community, NullLogger.Instance).Should().Contain("Pro");
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ValidateEdition_CommunityLayoutTemplate_Blocked()
    {
        LayoutTemplateRegistry.TryGetTemplate("Letter ANSI A Portrait", out var template);

        PrintingToolsRequestHandlers.ValidateEdition(template, "PNG32", HonuaEdition.Community, NullLogger.Instance).Should().Contain("Pro");
    }

    [UnitTest]
    [Protocol(Protocols.PrintingTools)]
    public void ValidateEdition_ProEdition_AllAllowed()
    {
        LayoutTemplateRegistry.TryGetTemplate("Letter ANSI A Portrait", out var template);

        PrintingToolsRequestHandlers.ValidateEdition(template, "PDF", HonuaEdition.Pro, NullLogger.Instance).Should().BeNull();
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
        PrintOutputFormat.IsSupported("PNG8").Should().BeFalse();
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
}
