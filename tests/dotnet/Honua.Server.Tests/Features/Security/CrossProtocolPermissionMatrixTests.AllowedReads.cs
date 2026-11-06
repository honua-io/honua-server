// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Security;

public sealed partial class CrossProtocolPermissionMatrixTests
{
    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task FeatureServerQuery_GrantScopedToTargetLayer_AllowsThatLayer()
    {
        using var factory = CreateFactory(Grant("query", layer: "Alpha Layer"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, GrantedRole);

        var response = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/query?where=1=1&f=json");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        document.RootElement.GetProperty("features").GetArrayLength().Should().BeGreaterThan(0);
    }

}
