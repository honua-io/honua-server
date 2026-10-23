// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Server.Features.Admin.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Security;

/// <summary>
/// Verifies the public URL environment alias with the shipped blank primary setting.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
public sealed class HostValidationEnvironmentAliasTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostValidation:Enabled"] = "true",
                ["HostValidation:RequireExplicitHosts"] = "true",
                ["Public:BaseUrl"] = string.Empty,
                ["PUBLIC_BASE_URL"] = "https://alias.honua.test"
            })));

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features")]
    public async Task StartupValidation_StrictProductionWithBlankPrimary_AcceptsRunningServerAliasConfiguration()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ogc/features?f=json");
        request.Headers.Host = "alias.honua.test";
        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var errors = ConfigurationValidationService.ValidateConfiguration(
            _fixture.GetService<IConfiguration>(), NullLogger.Instance,
            isDevelopment: false, isTest: false);

        errors.Should().NotContain(error =>
            error.Contains("Host validation is enabled", StringComparison.OrdinalIgnoreCase));
    }

    [IntegrationTheory]
    [InlineData("alias.honua.test", HttpStatusCode.OK)]
    [InlineData("attacker.example", HttpStatusCode.BadRequest)]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features")]
    public async Task Request_BlankPrimaryPublicUrl_UsesAliasAndRejectsForgedHost(string host, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ogc/features?f=json");
        request.Headers.Host = host;
        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
    }
}
