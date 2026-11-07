// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpPublishedCatalogReadbackTests
{
    [UnitTest]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.Mcp, "resources/read")]
    [Endpoint("POST /mcp resources/read honua://published-services")]
    [Endpoint("POST /mcp resources/read honua://published-services/{serviceId}")]
    public async Task Read_HiddenCatalogPublication_OmitsRootAndPreservesNamedRead()
    {
        var graph = Catalog();
        graph = graph with { Publications = graph.Publications.Select(publication => publication with { Advertised = false }).ToArray() };
        var named = await ReadAsync(graph);
        Assert.Equal(ServiceName, named.GetProperty("serviceId").GetString());
        var root = new PromotionSurfaceIndexResource(_promotions, _deployments, _jobs,
            NullLogger<PromotionSurfaceIndexResource>.Instance);
        var response = await root.ReadAsync(Context(graph), "honua://published-services", CancellationToken.None);
        var result = McpTestFactory.ParseJson(response.Contents[0].Text);
        Assert.Empty(result.GetProperty("items").EnumerateArray());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.Mcp, "resources/read")]
    [Endpoint("POST /mcp resources/read honua://published-services")]
    public async Task Read_HiddenCatalogIdentityCollidesWithPromotion_RefusesAmbiguousPublicUri()
    {
        var graph = Catalog();
        graph = graph with { Publications = graph.Publications.Select(publication => publication with { Advertised = false }).ToArray() };
        _promotions.ListActiveAsync(Arg.Any<CancellationToken>()).Returns([Promotion()]);
        _promotions.FindCollidingPublicIdsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns([ServiceName]);
        var root = new PromotionSurfaceIndexResource(_promotions, _deployments, _jobs,
            NullLogger<PromotionSurfaceIndexResource>.Instance);
        await Assert.ThrowsAsync<GeoprocessingValidationException>(() => root.ReadAsync(Context(graph), "honua://published-services", CancellationToken.None));
    }
}
