// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class GeocodingOperationsEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/geocoding/providers")]
    public async Task GetProviders_ReturnsHealthAndCapabilities()
    {
        var response = await _client.GetAsync("/api/v1/admin/operations/geocoding/providers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = root.GetProperty("data");
        data.TryGetProperty("providers", out var providers).Should().BeTrue();
        providers.ValueKind.Should().Be(JsonValueKind.Array);
        data.TryGetProperty("defaultProvider", out _).Should().BeTrue();
        data.TryGetProperty("failoverEnabled", out _).Should().BeTrue();
        data.TryGetProperty("generatedAt", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/geocoding/configuration")]
    public async Task GetConfiguration_ReturnsConfig()
    {
        var response = await _client.GetAsync("/api/v1/admin/operations/geocoding/configuration");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = root.GetProperty("data");
        data.TryGetProperty("enabled", out _).Should().BeTrue();
        data.TryGetProperty("defaultProvider", out _).Should().BeTrue();
        data.TryGetProperty("enableFailover", out _).Should().BeTrue();
        data.TryGetProperty("maxFailoverAttempts", out _).Should().BeTrue();
        data.TryGetProperty("enableCaching", out _).Should().BeTrue();
        data.TryGetProperty("cacheExpirationMinutes", out _).Should().BeTrue();
        data.TryGetProperty("defaultTimeoutSeconds", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/geocoding/providers")]
    public async Task GetProviders_WhenCoordinatorThrows_ReturnsSanitizedError()
    {
        var coordinator = Substitute.For<IGeocodeProviderCoordinator>();
        coordinator.CheckAllProvidersHealthAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Honua.Geocoding.Features.Geocoding.Domain.GeocodeProviderHealth>>>(_ =>
                throw new InvalidOperationException("provider secret connection string"));

        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IGeocodeProviderCoordinator>();
                services.AddSingleton(coordinator);
            });

        await fixture.InitializeAsync();
        try
        {
            var response = await fixture.CreateAdminClient().GetAsync("/api/v1/admin/operations/geocoding/providers");

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("Geocoding provider health check failed.");
            body.Should().NotContain("provider secret connection string");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
