// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpStyleToolTests
{
    private const string MirroredStyleId = "style-layer-42";

    // A layer's saved style is catalogued as "style-layer-{storage layer id}"; inlining it must still drop a hidden field.
    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_style")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_GetStyle_ByLayerMirroredStyle_InlinesNoClassOfAHiddenField()
    {
        var mirrored = Preset(MirroredStyleId) with
        {
            MapLibreStyleJson = """
                {"version":8,"layers":[{"id":"classes","type":"fill",
                  "paint":{"fill-color":["match",["get","notes"],"Confidential","#f00","#00f"]}}]}
                """
        };
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStylesForLayerAsync(StorageLayerId, Arg.Any<CancellationToken>()).Returns(new[] { mirrored });
        catalog.GetStyleAsync(MirroredStyleId, Arg.Any<CancellationToken>()).Returns(mirrored);

        var response = await DispatchAsync(
            GetStyleTool.ToolName,
            $$"""{ "serviceId": "{{ServiceId}}", "serviceLayerIndex": {{LayerIndex}}, "includeStylesheet": true }""",
            catalog: catalog,
            graphProvider: BuildGraphProviderWithHiddenNotes());

        response!.Error.Should().BeNull();
        var body = response.Result!.Value.GetProperty("structuredContent").GetProperty("encodings").EnumerateArray()
            .Single(encoding => encoding.GetProperty("encoding").GetString() == "mapbox-style")
            .GetProperty("inlineBody").GetString();
        body.Should().Contain("\"classes\"").And.NotContain("Confidential").And.NotContain("notes");
    }

    private static TestMetadataV2GraphProvider BuildGraphProviderWithHiddenNotes()
        => new TestMetadataV2GraphBuilder()
            .AddResource(ResourceId, "Parcels Dataset", fields: [new MetadataV2Field
            {
                Name = "notes",
                Type = MetadataV2FieldType.String,
                Hidden = true
            }])
            .AddStorageBinding("bind-parcels", ResourceId, "public.parcels", storageLayerId: StorageLayerId)
            .AddService(ServiceId, ServiceName)
            .AddPublication("pub-parcels", ServiceId, ResourceId, layerIndex: LayerIndex, storageBindingId: "bind-parcels")
            .BuildProvider();
}
