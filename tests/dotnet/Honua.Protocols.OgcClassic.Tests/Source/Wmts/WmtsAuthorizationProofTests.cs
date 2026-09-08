// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wmts;

/// <summary>
/// Authorization proof for WMTS <c>GetTile</c> (#4388). See
/// <see cref="OgcClassicAuthorizationProofTestBase"/> for the fixture and for the
/// shared denial floor this case asserts.
/// </summary>
[Collection("Database")]
public sealed class WmtsAuthorizationProofTests : OgcClassicAuthorizationProofTestBase
{
    [IntegrationTest]
    [Protocol(TestProtocols.Wmts10)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetTile")]
    public async Task Wmts_GetTile_WithoutReadRole_IsRefusedAndReturnsNoTileBytes()
    {
        var url =
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0" +
            $"&LAYER={WebAppFixture.TestLayerId}&STYLE=default&FORMAT=image/png" +
            "&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0";

        using var denied = await Outsider().GetAsync(url);
        await AssertRefusedWithoutPayloadAsync(denied, HttpStatusCode.Forbidden);

        using var allowed = await Viewer().GetAsync(url);
        await AssertImageAsync(allowed);
    }
}
