// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Server.Tests.Features.CrossServerConsume;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Import;

/// <summary>
/// Verifies the test-only cross-server consume proxy rejects unsafe source URLs.
/// </summary>
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.Consume)]
public sealed class ConsumeProbeEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync().ConfigureAwait(false);
    }

    [IntegrationTest]
    [Endpoint("GET /__test/cross-server-consume/proxy")]
    public async Task Proxy_WithTokenCredentialQueryInSourceUrl_ReturnsBadRequest()
    {
        const string sourceUrl = "http://127.0.0.1:65535/arcgis/services/Honua/MapServer/WMSServer?SERVICE=WMS&token=secret";

        using var response = await _fixture.Client.GetAsync(
            CrossServerConsumeTestSupport.BuildHonuaProxyUrl(sourceUrl)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("token credential query parameters");
        body.Should().NotContain("secret");
    }
}
