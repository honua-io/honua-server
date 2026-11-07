// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpMapToolTests
{
    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_list_layers")]
    [Endpoint("POST /mcp tools/call honua_describe_layer")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ListLayers_HiddenPublication_OmitsDiscoveryAndKeepsNamedDescription()
    {
        var provider = BuildGraphProvider();
        var graph = (await provider.GetCurrentAsync()).Graph;
        provider.SetGraph(graph with { Publications = graph.Publications.Select(publication => publication with { Advertised = false }).ToArray() });
        using var services = BuildServices(graphProvider: provider);
        var surface = BuildSurface();
        var listed = await surface.DispatchAsync(AuthenticatedContext(services), ToolCall("hidden-list", ListLayersTool.ToolName, "{}"), CancellationToken.None);
        Assert.Null(listed!.Error);
        var listing = listed.Result!.Value;
        Assert.False(listing.GetProperty("isError").GetBoolean());
        Assert.Empty(listing.GetProperty("structuredContent").GetProperty("layers").EnumerateArray());
        var described = await surface.DispatchAsync(AuthenticatedContext(services), ToolCall("hidden-description", DescribeLayerTool.ToolName,
            $$"""{"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}},"includeRowCount":false}"""), CancellationToken.None);
        Assert.Null(described!.Error);
        Assert.False(described.Result!.Value.GetProperty("isError").GetBoolean());
        Assert.Equal(ServiceId, described.Result.Value.GetProperty("structuredContent").GetProperty("serviceId").GetString());
    }
}
