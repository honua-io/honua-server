// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// File-backed metadata hosts register <see cref="IMetadataV2GraphProvider"/> and not
/// <see cref="IMetadataV2GraphStore"/>. GET settings must still resolve from the provider.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class ProviderOnlyServiceSettingsTests : IAsyncLifetime
{
    private const string AdminPassword = "provider-only-settings-admin-key";
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ProviderOnlyServiceSettingsTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            })
            .ConfigureServices(services => services.RemoveAll<IMetadataV2GraphStore>());
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/settings")]
    public async Task GetServiceSettings_WithoutAGraphStore_ReturnsTheProviderSnapshot()
    {
        _fixture.Services.GetService<IMetadataV2GraphStore>().Should().BeNull(
            "this host has a metadata provider and no graph store");

        var response = await _client.GetAsync("/api/v1/admin/services/test/settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("data").GetProperty("serviceName").GetString().Should().Be("test");
    }
}
