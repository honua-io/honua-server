// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

public sealed class CapabilityGateEndpointFilterTests
{
    private const string GatedDescriptorId = "test.experimental.capability";
    private const string PreviewDescriptorId = "test.preview.capability";

    private static readonly CapabilityDescriptor ExperimentalDescriptor = new()
    {
        Id = GatedDescriptorId,
        Category = "test",
        Kind = CapabilityKind.Feature,
        Maturity = CapabilityMaturity.Experimental,
    };

    private static readonly CapabilityDescriptor PreviewDescriptor = new()
    {
        Id = PreviewDescriptorId,
        Category = "test",
        Kind = CapabilityKind.Feature,
        Maturity = CapabilityMaturity.Preview,
    };

    [Fact]
    public async Task GatedEndpoint_WhenExperimentalDisabled_Returns404ProblemJson()
    {
        using var server = BuildServer(ExperimentalDescriptor, experimentalEnabled: false);
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
            .Should().Contain($"'{GatedDescriptorId}' capability is experimental and disabled");
        problem.GetProperty("detail").GetString()
            .Should().Contain($"Capabilities:Experimental:{GatedDescriptorId}");
    }

    [Fact]
    public async Task GatedEndpoint_WhenPreviewDisabled_UsesPreviewLifecycleInExistingProblemShape()
    {
        using var server = BuildServer(PreviewDescriptor, experimentalEnabled: false);
        using var client = server.CreateClient();

        var response = await client.GetAsync(new Uri("/gated/ping", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var content = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(content);

        problem.GetProperty("type").GetString()
            .Should().Be(CapabilityGateEndpointFilter.ExperimentalDisabledProblemType);
        problem.GetProperty("detail").GetString()
            .Should().Contain($"'{PreviewDescriptorId}' capability is preview and disabled");
        problem.GetProperty("detail").GetString()
            .Should().Contain($"Capabilities:Experimental:{PreviewDescriptorId}");
    }

    [Fact]
    public async Task GatedEndpoint_WhenExperimentalEnabled_Returns200()
    {
        using var server = BuildServer(ExperimentalDescriptor, experimentalEnabled: true);
        using var client = server.CreateClient();

        var response = await client.GetAsync(new Uri("/gated/ping", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("pong");
    }

    private static TestServer BuildServer(CapabilityDescriptor descriptor, bool experimentalEnabled)
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
                            new StubCapabilityRegistry(descriptor));
                        services.AddOptions<CapabilityFlagOptions>()
                            .Configure(options => options.Enabled = experimentalEnabled);
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            var group = endpoints.MapGroup("/gated")
                                .WithCapabilityGate(descriptor.Id);
                            group.MapGet("/ping", () => Results.Ok("pong"));
                        });
                    });
            });

        var host = builder.Build();
        host.Start();
        return host.GetTestServer();
    }

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
