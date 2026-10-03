// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using NSubstitute;
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
    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public async Task Execute_PublicationOutsideSubmitterTenant_SkipsStyleAndFeatureReads()
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service", Name = "service" },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active }
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource", Name = "resource" },
            StorageBindingIds = ["binding"], PrimaryStorageBindingId = "binding",
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active }
        };
        var snapshot = new MetadataV2GraphSnapshot(new MetadataV2Graph
        {
            Revision = 1, Services = [service], Resources = [resource],
            StorageBindings = [new MetadataV2StorageBinding
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "binding", Name = "binding" },
                ResourceId = "resource", StorageLayerId = 7,
                Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active }
            }],
            Publications = [new MetadataV2Publication
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "publication", Name = "publication", Tenant = "tenant-b" },
                ServiceId = "service", ResourceId = "resource", StorageBindingId = "binding", LayerIndex = 1,
                Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active }
            }]
        }, "print-test", DateTimeOffset.UnixEpoch);
        var validator = Substitute.For<IResourceValidator>();
        validator.ValidateServiceV2Async("service", Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(service));
        var graph = Substitute.For<IMetadataV2GraphProvider>();
        graph.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        var styles = Substitute.For<ILayerStyleCatalog>();
        var reader = Substitute.For<IFeatureReader>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", "tenant-a")], "test"));
        var map = new WebMapDefinition
        {
            OperationalLayers = [new WebMapOperationalLayer { Url = "/rest/services/service/MapServer/1" }]
        };

        await PrintingToolsRequestHandlers.ExecuteAsync(map, "PNG32", "MAP_ONLY", 96,
            validator, graph, reader, styles, NullLogger.Instance, default,
            callerPrincipal: principal, accessPolicyEvaluator: new AccessPolicyEvaluator());

        await styles.DidNotReceiveWithAnyArgs().GetLayerStyleAsync(default, default);
    }

    // --- ResolveFormat ---

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void ResolveFormat_Null_DefaultsToPng32()
    {
        PrintingToolsRequestHandlers.ResolveFormat(null).Should().Be("PNG32");
        PrintingToolsRequestHandlers.ResolveFormat("").Should().Be("PNG32");
        PrintingToolsRequestHandlers.ResolveFormat("  ").Should().Be("PNG32");
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void ResolveFormat_ValidFormat_PreservedTrimmed()
    {
        PrintingToolsRequestHandlers.ResolveFormat("PDF").Should().Be("PDF");
        PrintingToolsRequestHandlers.ResolveFormat(" JPG ").Should().Be("JPG");
    }

    // --- ResolveDpi ---

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void ResolveDpi_Default_Returns96()
    {
        var webMap = new WebMapDefinition();

        PrintingToolsRequestHandlers.ResolveDpi(webMap).Should().Be(96);
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void ResolveDpi_Null_Returns96()
    {
        PrintingToolsRequestHandlers.ResolveDpi(null).Should().Be(96);
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void ResolveDpi_ClampsToRange()
    {
        var low = new WebMapDefinition { ExportOptions = new WebMapExportOptions { Dpi = 10 } };
        PrintingToolsRequestHandlers.ResolveDpi(low).Should().Be(72);

        var high = new WebMapDefinition { ExportOptions = new WebMapExportOptions { Dpi = 9999 } };
        PrintingToolsRequestHandlers.ResolveDpi(high).Should().Be(600);
    }

    // --- ValidateEdition ---

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void ValidateEdition_CommunityMapOnlyPng_Allowed()
    {
        LayoutTemplateRegistry.TryGetTemplate("MAP_ONLY", out var template);

        PrintingToolsRequestHandlers.ValidateEdition(template, "PNG32", HonuaEdition.Community, NullLogger.Instance).Should().BeNull();
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void ValidateEdition_CommunityPdf_Blocked()
    {
        LayoutTemplateRegistry.TryGetTemplate("MAP_ONLY", out var template);

        PrintingToolsRequestHandlers.ValidateEdition(template, "PDF", HonuaEdition.Community, NullLogger.Instance).Should().Contain("Pro");
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void ValidateEdition_CommunityLayoutTemplate_Blocked()
    {
        LayoutTemplateRegistry.TryGetTemplate("Letter ANSI A Portrait", out var template);

        PrintingToolsRequestHandlers.ValidateEdition(template, "PNG32", HonuaEdition.Community, NullLogger.Instance).Should().Contain("Pro");
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void ValidateEdition_ProEdition_AllAllowed()
    {
        LayoutTemplateRegistry.TryGetTemplate("Letter ANSI A Portrait", out var template);

        PrintingToolsRequestHandlers.ValidateEdition(template, "PDF", HonuaEdition.Pro, NullLogger.Instance).Should().BeNull();
    }

    // --- CollectWarnings ---

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
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
    [Protocol(TestProtocols.PrintingTools)]
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
    [Protocol(TestProtocols.PrintingTools)]
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
    [Protocol(TestProtocols.PrintingTools)]
    public void PrintOutputFormat_GetContentType_ReturnsCorrectMimeTypes()
    {
        PrintOutputFormat.GetContentType("PDF").Should().Be("application/pdf");
        PrintOutputFormat.GetContentType("PNG32").Should().Be("image/png");
        PrintOutputFormat.GetContentType("JPG").Should().Be("image/jpeg");
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void PrintOutputFormat_GetExtension_ReturnsCorrectExtensions()
    {
        PrintOutputFormat.GetExtension("PDF").Should().Be(".pdf");
        PrintOutputFormat.GetExtension("PNG32").Should().Be(".png");
        PrintOutputFormat.GetExtension("JPG").Should().Be(".jpg");
    }
}
