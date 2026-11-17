// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Studio.Drafts;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Studio;

public sealed class MapDraftFactoryTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [UnitTest]
    public void CreateDraft_WithStructuredInput_ReturnsPackageWithStableMapIdentifier()
    {
        var result = CreateFactory().CreateDraft(FullRequest());

        Assert.Empty(result.Errors);
        Assert.True(result.Succeeded);
        var package = Assert.IsType<Map>(result.Map);
        Assert.StartsWith("map_", package.MapId, StringComparison.Ordinal);
    }

    [UnitTest]
    public void CreateDraft_ServerOwnsFormatAndStatusAndCreatedAt()
    {
        // Retained knowledge §5: format and status are server-owned discriminators.
        var package = CreateFactory().CreateDraft(FullRequest()).Map!;

        Assert.Equal("honua_map.v1", package.Format);
        Assert.Equal(PackageStatus.Draft, package.Status);
        Assert.Equal(FixedNow, package.CreatedAt);
    }

    [UnitTest]
    public void CreateDraft_HonoursSourceBindingsAndInitialView()
    {
        // The two defects ADR-0076 calls out: sourceBindings was rejected outright
        // and initialView was advertised then silently dropped.
        var package = CreateFactory().CreateDraft(FullRequest()).Map!;

        var binding = Assert.Single(package.SourceBindings);
        Assert.Equal("parcels", binding.SourceId);
        Assert.Equal(SourceProtocol.OgcFeatures, binding.Protocol);
        Assert.Equal("https://example.test/ogc", binding.Locator.Url);

        Assert.NotNull(package.InitialView);
        Assert.Equal([-97.95, 30.15, -97.55, 30.55], package.InitialView!.Bbox);
        Assert.Equal("EPSG:4326", package.InitialView.Crs);

        Assert.Equal("analysis_default", package.TemplateId);
        Assert.Equal("theme_operational_dark", package.ThemeId);
        Assert.Equal("style_choropleth", Assert.Single(package.StyleRefs).StyleId);
    }

    [UnitTest]
    public void CreateDraft_WithIdenticalInput_IsDeterministic()
    {
        // Compared as serialized JSON rather than by record equality: the bbox is
        // a double[], whose default equality is by reference.
        var factory = CreateFactory();

        var first = factory.CreateDraft(FullRequest()).Map!;
        var second = factory.CreateDraft(FullRequest()).Map!;

        Assert.Equal(Serialize(first), Serialize(second));
    }

    [UnitTest]
    public void CreateDraft_WithUnmodelledPromptText_ProducesTheSameDraft()
    {
        // There is no prompt member to supply, which is the point: prose cannot
        // reach the factory and therefore cannot change what it produces. This
        // pins that the request record carries no natural-language surface.
        Assert.DoesNotContain(
            typeof(MapDraftRequest).GetProperties(),
            property => property.Name.Contains("Prompt", StringComparison.OrdinalIgnoreCase));

        var factory = CreateFactory();
        var withStyle = factory.CreateDraft(FullRequest()).Map!;
        var alsoWithStyle = factory.CreateDraft(FullRequest() with { }).Map!;

        Assert.Equal(Serialize(withStyle), Serialize(alsoWithStyle));
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("EPSG:3857")]
    [InlineData("EPSG:4326")]
    [InlineData("http://www.opengis.net/def/crs/EPSG/0/4326")]
    [InlineData("urn:ogc:def:crs:EPSG::4326")]
    public void CreateDraft_WithSupportedCrsForm_IsAccepted(string crs)
    {
        var result = CreateFactory().CreateDraft(FullRequest() with
        {
            InitialView = new MapInitialViewInput { Bbox = [-1, -1, 1, 1], Crs = crs }
        });

        Assert.Empty(result.Errors);
        Assert.Equal(crs, result.Map!.InitialView!.Crs);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("4326")]
    [InlineData("EPSG:")]
    [InlineData("EPSG:abcd")]
    [InlineData("CRS84")]
    [InlineData("https://www.opengis.net/def/crs/EPSG/0/4326")]
    [InlineData("urn:ogc:def:crs:")]
    public void CreateDraft_WithUnsupportedCrsForm_IsRejected(string crs)
    {
        var result = CreateFactory().CreateDraft(FullRequest() with
        {
            InitialView = new MapInitialViewInput { Bbox = [-1, -1, 1, 1], Crs = crs }
        });

        Assert.Null(result.Map);
        Assert.Contains(result.Errors, error => error.Code == "crsUnsupported");
    }

    [UnitTest]
    public void CreateDraft_WithoutCrs_DefaultsToEpsg4326()
    {
        var result = CreateFactory().CreateDraft(FullRequest() with
        {
            InitialView = new MapInitialViewInput { Bbox = [-1, -1, 1, 1] }
        });

        Assert.Equal("EPSG:4326", result.Map!.InitialView!.Crs);
    }

    [UnitTest]
    public void CreateDraft_WithReversedLongitudeAxis_IsRejected()
    {
        // Axis order is [minLon, minLat, maxLon, maxLat] and min <= max is
        // validated on both axes (retained knowledge §4).
        var result = CreateFactory().CreateDraft(FullRequest() with
        {
            InitialView = new MapInitialViewInput { Bbox = [10, -1, -10, 1] }
        });

        Assert.Null(result.Map);
        Assert.Contains(result.Errors, error => error.Code == "bboxNotOrdered" && error.Message.Contains("minLon", StringComparison.Ordinal));
    }

    [UnitTest]
    public void CreateDraft_WithReversedLatitudeAxis_IsRejected()
    {
        var result = CreateFactory().CreateDraft(FullRequest() with
        {
            InitialView = new MapInitialViewInput { Bbox = [-10, 10, 10, -10] }
        });

        Assert.Null(result.Map);
        Assert.Contains(result.Errors, error => error.Code == "bboxNotOrdered" && error.Message.Contains("minLat", StringComparison.Ordinal));
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public void CreateDraft_WithWrongBboxArity_IsRejected(int count)
    {
        var result = CreateFactory().CreateDraft(FullRequest() with
        {
            InitialView = new MapInitialViewInput { Bbox = Enumerable.Repeat(1.0, count).ToArray() }
        });

        Assert.Null(result.Map);
        Assert.Contains(result.Errors, error => error.Code == "bboxInvalid");
    }

    [UnitTest]
    public void CreateDraft_WithoutInitialView_LeavesTheViewportUnset()
    {
        // The deterministic path has no place name to infer an extent from, so it
        // invents nothing rather than guessing a world extent.
        var result = CreateFactory().CreateDraft(FullRequest() with { InitialView = null });

        Assert.Empty(result.Errors);
        Assert.Null(result.Map!.InitialView);
    }

    [UnitTest]
    public void CreateDraft_WithSourceMissingLocator_WarnsRatherThanFails()
    {
        // Retained knowledge §2: a missing locator URL is deliberately a warning.
        var result = CreateFactory().CreateDraft(FullRequest() with
        {
            SourceBindings = [new SourceBindingInput { SourceId = "parcels", Protocol = "ogc_features" }]
        });

        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, warning => warning.Code == "sourceNotResolved");
        Assert.Equal("honua://unresolved/parcels", result.Map!.SourceBindings[0].Locator.Url);
    }

    [UnitTest]
    public void CreateDraft_WithBlankStyleId_IsRejected()
    {
        // Retained knowledge §2: a present styleRef with a blank styleId blocks.
        var result = CreateFactory().CreateDraft(FullRequest() with { StyleId = "  " });

        Assert.Null(result.Map);
        Assert.Contains(result.Errors, error => error.Code == "styleRefInvalid");
    }

    [UnitTest]
    public void CreateDraft_WithoutStyleId_ProducesNoStyleRefs()
    {
        var result = CreateFactory().CreateDraft(FullRequest() with { StyleId = null });

        Assert.Empty(result.Errors);
        Assert.Empty(result.Map!.StyleRefs);
    }

    [UnitTest]
    public void CreateDraft_WithUnknownProtocol_IsRejected()
    {
        var result = CreateFactory().CreateDraft(FullRequest() with
        {
            SourceBindings = [new SourceBindingInput { SourceId = "parcels", Protocol = "ogc_api_features" }]
        });

        Assert.Null(result.Map);
        Assert.Contains(result.Errors, error => error.Code == "sourceProtocolInvalid");
    }

    [UnitTest]
    public void CreateDraft_WithBlankSourceId_IsRejected()
    {
        var result = CreateFactory().CreateDraft(FullRequest() with
        {
            SourceBindings = [new SourceBindingInput { SourceId = " ", Protocol = "ogc_features" }]
        });

        Assert.Null(result.Map);
        Assert.Contains(result.Errors, error => error.Code == "sourceIdMissing");
    }

    [UnitTest]
    public void CreateDraft_WithDuplicateSourceIds_IsRejected()
    {
        var result = CreateFactory().CreateDraft(FullRequest() with
        {
            SourceBindings =
            [
                new SourceBindingInput { SourceId = "parcels", Protocol = "ogc_features", Url = "https://example.test/a" },
                new SourceBindingInput { SourceId = "parcels", Protocol = "ogc_features", Url = "https://example.test/b" }
            ]
        });

        Assert.Null(result.Map);
        Assert.Contains(result.Errors, error => error.Code == "sourceIdDuplicate");
    }

    [UnitTest]
    public void CreateDraft_PreservesSourceBindingOrder()
    {
        var result = CreateFactory().CreateDraft(FullRequest() with
        {
            SourceBindings =
            [
                new SourceBindingInput { SourceId = "b", Protocol = "wms", Url = "https://example.test/b" },
                new SourceBindingInput { SourceId = "a", Protocol = "wfs", Url = "https://example.test/a" }
            ]
        });

        Assert.Equal(["b", "a"], result.Map!.SourceBindings.Select(binding => binding.SourceId));
    }

    private static MapDraftFactory CreateFactory() => new(
        new StubIdentifierGenerator(),
        new FixedTimeProvider(FixedNow));

    private static MapDraftRequest FullRequest() => new()
    {
        TemplateId = "analysis_default",
        StyleId = "style_choropleth",
        ThemeId = "theme_operational_dark",
        SourceBindings =
        [
            new SourceBindingInput
            {
                SourceId = "parcels",
                Protocol = "ogc_features",
                Url = "https://example.test/ogc"
            }
        ],
        InitialView = new MapInitialViewInput { Bbox = [-97.95, 30.15, -97.55, 30.55] }
    };

    private static string Serialize(Map package) =>
        JsonSerializer.Serialize(package, PackagingJsonContext.Default.Map);

    private sealed class StubIdentifierGenerator : IDraftIdentifierGenerator
    {
        public string NewIdentifier(string prefix) => prefix + "_fixed0001";
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
