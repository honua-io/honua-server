// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Catalog;

public sealed partial class HiddenPublicationTests
{
    [IntegrationTheory]
    [Protocol(TestProtocols.ODataV4, TestProtocols.MapServer)]
    [Operation(Operations.GetMetadata)]
    [InlineData("/odata/Layers", "value")]
    [InlineData("/rest/services/{service}/MapServer", "layers")]
    [InlineData("/rest/services/{service}/MapServer/layers", "layers")]
    [InlineData("/rest/services/{service}/MapServer/legend", "layers")]
    [Endpoint("GET /odata/Layers")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/layers")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/legend")]
    public async Task Discovery_HiddenPublications_RemoveLayerNames(string template, string property)
    {
        var route = template.Replace("{service}", WebAppFixture.TestServiceId, StringComparison.Ordinal) + (template.StartsWith("/odata", StringComparison.Ordinal) ? "?$format=json" : "?f=json");
        using var before = await _fixture.Client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        using var visible = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
        Assert.NotEmpty(visible.RootElement.GetProperty(property).EnumerateArray());
        SetAdvertising(false);
        using var after = await _fixture.Client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        using var hidden = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        Assert.Empty(hidden.RootElement.GetProperty(property).EnumerateArray());
    }
}
