// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Studio;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Protocol(TestProtocols.Mcp)]
public sealed class StudioCompositionDocumentMcpToolTests
{
    private const string MapSpec = """
        {"version":8,"sources":{"parcels":{"type":"geojson","data":{}},"roads":{"type":"geojson","data":{}}},
         "layers":[{"id":"parcels","type":"fill","source":"parcels"},{"id":"roads","type":"line","source":"roads"}],
         "metadata":{"honua:compositionLayerIds":["parcels","roads"]}}
        """;

    private const string SeededBody = """
        {"mapPackageId":"parity-map","format":"honua_map_package.v1",
         "sourceBindings":[{"sourceId":"parcels","protocol":"ogc_features","locator":{}},
                           {"sourceId":"roads","protocol":"ogc_features","locator":{}}],
         "styleRefs":[{"styleId":"honua-inline:roads","body":{"roads":{"paint":{"line-color":"#334455"}}}}],
         "layers":[{"id":"parcels","sourceId":"parcels","visible":true},
                   {"id":"roads","sourceId":"roads","visible":true,"styleRef":"honua-inline:roads"}],
         "view":{"center":[-157.86,21.3],"zoom":9},
         "widgets":[],
         "mapSpec":
        """ + MapSpec + "}";

    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_remove_layer")]
    public async Task RemoveLayer_PrunesThePortableMembersOnlyThatLayerUsed()
    {
        var draft = await StudioMcpDraftHarness.SeedMapDraftAsync(_jobService, SeededBody);

        var body = await draft.CallAndReadBodyAsync(
            new RemoveStudioLayerTool(_jobService, NullLogger<RemoveStudioLayerTool>.Instance),
            """{"layerId":"roads"}""");

        body.GetProperty("sourceBindings").EnumerateArray().Select(SourceIdOf).Should().Equal("parcels");
        body.GetProperty("styleRefs").GetArrayLength().Should().Be(0);
        body.GetProperty("mapSpec").GetProperty("layers").EnumerateArray().Select(IdOf).Should().Equal("parcels");
        body.GetProperty("mapSpec").GetProperty("sources").TryGetProperty("roads", out _).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_set_view")]
    public async Task SetView_WithAPartialView_KeepsTheCurrentCenter()
    {
        var draft = await StudioMcpDraftHarness.SeedMapDraftAsync(_jobService, SeededBody);

        var body = await draft.CallAndReadBodyAsync(
            new SetStudioViewTool(_jobService, NullLogger<SetStudioViewTool>.Instance),
            """{"view":{"zoom":12}}""");

        body.GetProperty("view").GetProperty("zoom").GetDouble().Should().Be(12);
        body.GetProperty("view").GetProperty("center")[0].GetDouble().Should().Be(-157.86);
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_set_view")]
    public async Task SetView_WithAZoomOutOfBounds_SurfacesAnOutOfBoundsViolation()
    {
        var draft = await StudioMcpDraftHarness.SeedMapDraftAsync(_jobService, SeededBody);

        var error = await draft.CallExpectingErrorAsync(
            new SetStudioViewTool(_jobService, NullLogger<SetStudioViewTool>.Instance),
            """{"view":{"zoom":30}}""");

        error.GetProperty("code").GetString().Should().Be("invalid_argument");
        error.GetProperty("message").GetString().Should().Be("setView: zoom 30 is out of bounds [0, 24]");
        error.GetProperty("violations")[0].GetProperty("code").GetString().Should().Be("out-of-bounds");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_set_layer_style")]
    public async Task SetLayerStyle_WithAStyleBody_BindsTheLayerToItsInlineStyle()
    {
        var draft = await StudioMcpDraftHarness.SeedMapDraftAsync(_jobService, SeededBody);

        var body = await draft.CallAndReadBodyAsync(
            new SetStudioLayerStyleTool(_jobService, NullLogger<SetStudioLayerStyleTool>.Instance),
            """{"layerId":"parcels","styleBody":{"parcels":{"paint":{"fill-color":"#112233"}}}}""");

        body.GetProperty("styleRefs").EnumerateArray().Select(style => style.GetProperty("styleId").GetString())
            .Should().Equal("honua-inline:roads", "honua-inline:parcels");
        body.GetProperty("layers")[0].GetProperty("styleRef").GetString().Should().Be("honua-inline:parcels");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_set_layer_style")]
    public async Task SetLayerStyle_WithBothAStyleBodyAndAStyleRef_IsRefused()
    {
        var draft = await StudioMcpDraftHarness.SeedMapDraftAsync(_jobService, SeededBody);

        var error = await draft.CallExpectingErrorAsync(
            new SetStudioLayerStyleTool(_jobService, NullLogger<SetStudioLayerStyleTool>.Instance),
            """{"layerId":"parcels","styleRef":"shared","styleBody":{"parcels":{}}}""");

        error.GetProperty("code").GetString().Should().Be("invalid_argument");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_set_layer_style")]
    public async Task SetLayerStyle_WithANullStyleBody_IsRefusedAndLeavesTheDraftUnchanged()
    {
        var draft = await StudioMcpDraftHarness.SeedMapDraftAsync(_jobService, SeededBody);
        var before = await draft.ReadDraftAsync();

        var error = await draft.CallExpectingErrorAsync(
            new SetStudioLayerStyleTool(_jobService, NullLogger<SetStudioLayerStyleTool>.Instance),
            """{"layerId":"roads","styleBody":null}""");

        error.GetProperty("code").GetString().Should().Be("invalid_argument");
        error.GetProperty("message").GetString().Should().Be("Style overrides must be an object.");
        var after = await draft.ReadDraftAsync();
        after.GetProperty("generation").GetInt64().Should().Be(before.GetProperty("generation").GetInt64());
        after.GetProperty("envelope").GetRawText().Should().Be(before.GetProperty("envelope").GetRawText());
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_set_map_style")]
    public async Task SetMapStyle_WithARestyledLayer_StoresTheStyleAndClearsStyleBindings()
    {
        var draft = await StudioMcpDraftHarness.SeedMapDraftAsync(_jobService, SeededBody);
        var restyled = MapSpec.Replace("\"type\":\"line\",", "\"type\":\"line\",\"paint\":{\"line-width\":3},", StringComparison.Ordinal);

        var body = await draft.CallAndReadBodyAsync(
            new SetStudioMapStyleTool(_jobService, NullLogger<SetStudioMapStyleTool>.Instance),
            "{\"mapSpec\":" + restyled + "}");

        body.GetProperty("mapSpec").GetProperty("layers")[1].GetProperty("paint").GetProperty("line-width").GetInt32().Should().Be(3);
        body.GetProperty("styleRefs").GetArrayLength().Should().Be(0);
        body.GetProperty("layers")[1].TryGetProperty("styleRef", out _).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_set_map_style")]
    public async Task SetMapStyle_WhenTheEditAddsASource_SurfacesInvalidArgument()
    {
        var draft = await StudioMcpDraftHarness.SeedMapDraftAsync(_jobService, SeededBody);
        var widened = MapSpec.Replace("\"sources\":{", "\"sources\":{\"extra\":{\"type\":\"geojson\"},", StringComparison.Ordinal);

        var error = await draft.CallExpectingErrorAsync(
            new SetStudioMapStyleTool(_jobService, NullLogger<SetStudioMapStyleTool>.Instance),
            "{\"mapSpec\":" + widened + "}");

        error.GetProperty("message").GetString()
            .Should().Be("The editor cannot change protected style member 'sources'.");
    }

    private static string? SourceIdOf(JsonElement binding) => binding.GetProperty("sourceId").GetString();

    private static string? IdOf(JsonElement layer) => layer.GetProperty("id").GetString();
}
