// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Security;

public sealed partial class ODataServiceRbacTests
{
    [IntegrationTheory]
    [InlineData("/odata/Features(0)")]
    [InlineData("/odata/Layers(0)/Features")]
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    [Endpoint("GET /odata/Layers({layerId})/Features")]
    public async Task ReadFeatures_WithAllowedRole_ReturnsStoredRows(string path)
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () => new RbacTestLayerCatalog(
            alphaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["alpha-reader"])));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-reader");

        var response = await client.GetAsync(path);
        var payload = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToArray();
        features.Should().NotBeEmpty();
        features.Select(ODataTestHelpers.ParseAttributes).Select(attributes => attributes.GetProperty("name").GetString())
            .Should().Contain("Test Feature");
    }
}
