// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Infrastructure.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Features.Capabilities;

/// <summary>
/// Component coverage for the shared <see cref="CapabilityGateEndpointFilter"/>
/// (Track T5 / #2341): a group-level gate that short-circuits with 404
/// <c>application/problem+json</c> when its capability descriptor resolves
/// experimental-disabled, and passes through when the experimental flag is on.
/// </summary>
public sealed class CapabilityGateEndpointFilterTests
{
    private const string GatedDescriptorId = "test.experimental.capability";

    private static readonly CapabilityDescriptor ExperimentalDescriptor = new()
    {
        Id = GatedDescriptorId,
        Category = "test",
        Kind = CapabilityKind.Feature,
        Maturity = CapabilityMaturity.Experimental,
    };

    [Fact]
    public async Task GatedEndpoint_WhenExperimentalDisabled_Returns404ProblemJson()
    {
        using var server = BuildServer(experimentalEnabled: false);
        using var client = server.CreateClient();

        var response = await client.GetAsync(new Uri("/gated/ping", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var content = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(content);

        problem.GetProperty("type").GetString()
            .Should().Be(CapabilityGateEndpointFilter.ExperimentalDisabledProblemType);
        problem.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status404NotFound);
        problem.GetProperty("detail").GetString()
            .Should().Contain($"Capabilities:Experimental:{GatedDescriptorId}");
    }

    [Fact]
    public async Task GatedEndpoint_WhenExperimentalEnabled_Returns200()
    {
        using var server = BuildServer(experimentalEnabled: true);
        using var client = server.CreateClient();

        var response = await client.GetAsync(new Uri("/gated/ping", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("pong");
    }

    private static TestServer BuildServer(bool experimentalEnabled)
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddLogging();
                        services.AddRouting();
                        services.AddSingleton<ICapabilityRegistry>(
                            new StubCapabilityRegistry(ExperimentalDescriptor));
                        services.AddOptions<CapabilityFlagOptions>()
                            .Configure(options => options.Enabled = experimentalEnabled);
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            var group = endpoints.MapGroup("/gated")
                                .WithCapabilityGate(GatedDescriptorId);
                            group.MapGet("/ping", () => Results.Ok("pong"));
                        });
                    });
            });

        var host = builder.Build();
        host.Start();
        return host.GetTestServer();
    }

    /// <summary>
    /// Minimal <see cref="ICapabilityRegistry"/> that exposes a single experimental
    /// descriptor and defers resolution to the shared
    /// <see cref="CapabilityGateResolver"/>, mirroring the production registry.
    /// </summary>
    private sealed class StubCapabilityRegistry : ICapabilityRegistry
    {
        private readonly CapabilityDescriptor _descriptor;

        public StubCapabilityRegistry(CapabilityDescriptor descriptor) => _descriptor = descriptor;

        public IReadOnlyList<CapabilityDescriptor> All => [_descriptor];

        public CapabilityDescriptor? Find(string id)
            => string.Equals(id, _descriptor.Id, StringComparison.Ordinal) ? _descriptor : null;

        public CapabilityResolution Resolve(string id, CapabilityGateContext context)
            => CapabilityGateResolver.Resolve(Find(id), context);
    }
}
