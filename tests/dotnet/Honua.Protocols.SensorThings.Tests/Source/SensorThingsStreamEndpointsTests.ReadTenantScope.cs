// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

// In multi-tenant mode a sensing read needs the same resolved tenant scope as a subscription.
public sealed partial class SensorThingsStreamEndpointsTests
{
    [IntegrationTheory]
    [InlineData(null, HttpStatusCode.Forbidden)]
    [InlineData("tenant-a", HttpStatusCode.OK)]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things")]
    public async Task Read_NonAdmin_FollowsResolvedTenantScope(string? tenantId, HttpStatusCode expected)
    {
        const string referer = "https://sensing-read-proof.example/";
        var credential = await _fixture.GetService<IPortalTokenIssuer>().IssueAsync(
            new PortalTokenIssueRequest("sensing-read-scope", "Sensing read scope", tenantId, ["reader"],
                PortalTokenClientType.Referer, referer, DateTimeOffset.UtcNow.AddMinutes(1)),
            CancellationToken.None);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/sta/v1.1/Things?token={credential.Token}");
        request.Headers.Referrer = new Uri(referer);

        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
    }
}
