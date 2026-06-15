// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Plugins;
using Honua.Plugins.Abstractions;
using Honua.Sample.UtilityValidationPlugin;
using Honua.Server.Features.Plugins;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Honua.Server.Tests.Features.Plugins;

/// <summary>
/// Integration coverage for plugin-contributed REST routes (<see cref="ICustomEndpoint"/>, #1562)
/// mapped via <see cref="PluginCustomEndpoints.MapHonuaPluginEndpoints"/>. Uses a minimal
/// self-hosted <see cref="TestServer"/> rather than the full server factory: the route is only
/// present when a custom-endpoint plugin is compiled in, which the default host does not do.
/// </summary>
[Protocol(TestProtocols.Admin)]
public sealed class PluginCustomEndpointsTests
{
    private const string Route = "/plugins/utility/status";

    private static TestServer CreateServer(HonuaEdition edition, bool enabled = true)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddLogging();
                        services.AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(edition));
                        services.AddSingleton<IAuditLog, NullAuditLog>();

                        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["Plugins:Enabled"] = enabled ? "true" : "false",
                            })
                            .Build();
                        services.AddHonuaPlugins(configuration, p => p.Add<UtilityStatusEndpointPlugin>());
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapHonuaPluginEndpoints());
                    });
            })
            .Build();

        host.Start();
        return host.GetTestServer();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /plugins/utility/status")]
    public async Task CustomEndpoint_ReturnsPluginResponse_WhenEntitled()
    {
        using var server = CreateServer(HonuaEdition.Enterprise);
        using var client = server.CreateClient();

        var response = await client.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("utility-status-endpoint");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /plugins/utility/status")]
    public async Task CustomEndpoint_NotFound_WhenUnlicensed()
    {
        using var server = CreateServer(HonuaEdition.Community);
        using var client = server.CreateClient();

        var response = await client.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "plugin-contributed routes are gated behind the Enterprise plugin.sdk entitlement");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /plugins/utility/status")]
    public async Task CustomEndpoint_NotFound_WhenKillSwitchDisabled()
    {
        using var server = CreateServer(HonuaEdition.Enterprise, enabled: false);
        using var client = server.CreateClient();

        var response = await client.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the operator kill-switch (Plugins:Enabled=false) disables contributed routes");
    }
}
