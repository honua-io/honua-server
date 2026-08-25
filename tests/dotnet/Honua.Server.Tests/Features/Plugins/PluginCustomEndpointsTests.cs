// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Middleware;
using Honua.Plugins;
using Honua.Plugins.Abstractions;
using Honua.Sample.UtilityValidationPlugin;
using Honua.Server.Features.Plugins;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
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
                        services.AddHonuaHeadRequestSupport();
                        services.AddHonuaPlugins(configuration, p =>
                        {
                            p.Add<UtilityStatusEndpointPlugin>();
                            p.Add<SplitGetEndpointPlugin>();
                            p.Add<SplitHeadEndpointPlugin>();
                            p.Add<HeadOnlyEndpointPlugin>();
                        });
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseHonuaHeadRequestMethod();
                        app.UseHonuaHeadRequestGetSemantics();
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

    [IntegrationTest]
    public async Task CustomEndpoint_SeparateExplicitHeadRoute_SelectsHeadPlugin()
    {
        using var server = CreateServer(HonuaEdition.Enterprise);
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, "/plugins/split");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [IntegrationTest]
    public async Task CustomEndpoint_SeparateExplicitHeadRoute_DoesNotCaptureGet()
    {
        using var server = CreateServer(HonuaEdition.Enterprise);
        using var client = server.CreateClient();

        using var response = await client.GetAsync("/plugins/split");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("split-get");
    }

    [IntegrationTest]
    public async Task CustomEndpoint_HeadOnlyRoute_GetReturns405WithDeclaredAllowHeader()
    {
        using var server = CreateServer(HonuaEdition.Enterprise);
        using var client = server.CreateClient();

        using var response = await client.GetAsync("/plugins/head-only");

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        response.Content.Headers.Allow.Should().ContainSingle().Which.Should().Be("HEAD");
    }

    [IntegrationTest]
    public void CustomEndpoint_HeadOnlyRoute_PublicMetadataRemainsHeadOnly()
    {
        using var server = CreateServer(HonuaEdition.Enterprise);
        var endpoints = server.Services.GetServices<EndpointDataSource>()
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(static endpoint => endpoint.RoutePattern.RawText == "/plugins/head-only")
            .ToArray();

        var publicEndpoint = endpoints.Should().ContainSingle(endpoint =>
                endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>() == null)
            .Which;
        publicEndpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Should().Equal("HEAD");

        var fallbackEndpoint = endpoints.Should().ContainSingle(endpoint =>
                endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>() != null)
            .Which;
        fallbackEndpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Should().Equal("GET");
    }

    [Plugin("split-get", "1.0.0", Capabilities = PluginCapability.CustomEndpoints)]
    public sealed class SplitGetEndpointPlugin : ICustomEndpoint
    {
        public IReadOnlyList<string> Methods { get; } = ["GET"];

        public string Pattern => "split";

        public bool RequiresAuthorization => false;

        public ValueTask<PluginEndpointResponse> HandleAsync(
            PluginEndpointRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(PluginEndpointResponse.Json("{\"handler\":\"split-get\"}"));
    }

    [Plugin("split-head", "1.0.0", Capabilities = PluginCapability.CustomEndpoints)]
    public sealed class SplitHeadEndpointPlugin : ICustomEndpoint
    {
        public IReadOnlyList<string> Methods { get; } = ["HEAD"];

        public string Pattern => "split";

        public bool RequiresAuthorization => false;

        public ValueTask<PluginEndpointResponse> HandleAsync(
            PluginEndpointRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(PluginEndpointResponse.Status(StatusCodes.Status202Accepted));
    }

    [Plugin("head-only", "1.0.0", Capabilities = PluginCapability.CustomEndpoints)]
    public sealed class HeadOnlyEndpointPlugin : ICustomEndpoint
    {
        public IReadOnlyList<string> Methods { get; } = ["HEAD"];

        public string Pattern => "head-only";

        public bool RequiresAuthorization => false;

        public ValueTask<PluginEndpointResponse> HandleAsync(
            PluginEndpointRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(PluginEndpointResponse.Status(StatusCodes.Status202Accepted));
    }
}
