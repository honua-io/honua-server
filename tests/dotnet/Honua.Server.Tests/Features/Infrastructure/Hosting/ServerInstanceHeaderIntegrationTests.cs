// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Hosting;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Infrastructure.Hosting;

/// <summary>
/// Proves the per-instance identity through the real server pipeline: the <c>Honua-Instance</c>
/// header on success, probe, and not-found responses, and the admin version <c>instance</c> block.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Metadata)]
public sealed class ServerInstanceHeaderIntegrationTests : IAsyncLifetime
{
    private const string InstanceId = "it-replica-a";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ServerInstanceIdentity.ConfigurationKey] = InstanceId
            })));

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /healthz/live")]
    public async Task LivenessProbe_CarriesInstanceHeader_AndKeepsPlainTextBody()
    {
        using var response = await _fixture.Client.GetAsync("/healthz/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues(ServerInstanceIdentity.HeaderName).Should().ContainSingle().Which.Should().Be(InstanceId);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    [IntegrationTest]
    [Endpoint("GET /healthz/ready")]
    public async Task ReadinessProbe_CarriesInstanceHeader()
    {
        using var response = await _fixture.Client.GetAsync("/healthz/ready");

        response.Headers.GetValues(ServerInstanceIdentity.HeaderName).Should().ContainSingle().Which.Should().Be(InstanceId);
    }

    [IntegrationTest]
    public async Task UnknownRoute_StillCarriesInstanceHeader()
    {
        using var response = await _fixture.Client.GetAsync("/no-such-route-for-instance-identity");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.GetValues(ServerInstanceIdentity.HeaderName).Should().ContainSingle().Which.Should().Be(InstanceId);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/version")]
    public async Task AdminVersion_ReportsInstanceBlockMatchingTheHeader()
    {
        using var response = await _fixture.Client.GetAsync("/api/v1/admin/version");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var header = response.Headers.GetValues(ServerInstanceIdentity.HeaderName).Should().ContainSingle().Which;

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var instance = document.RootElement.GetProperty("data").GetProperty("instance");
        instance.GetProperty("id").GetString().Should().Be(InstanceId).And.Be(header);
        instance.GetProperty("source").GetString().Should().Be(ServerInstanceIdentitySources.Configuration);
        instance.GetProperty("startedAt").GetDateTimeOffset().Should().BeBefore(DateTimeOffset.UtcNow.AddSeconds(1));
    }
}
