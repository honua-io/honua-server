// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
/// Component coverage for <see cref="CapabilityGateOpenApiDocumentTransformer"/>
/// (Track T7 / #2343): the transformer wired into the <c>AddOpenApi</c> pipeline via
/// <c>AddCapabilityGatedOpenApi</c> must prune an operation gated on a
/// disabled-experimental capability from the generated OpenAPI document, while
/// leaving enabled/ungated operations in place — the description-layer counterpart
/// to the runtime 404 gate (T5).
/// </summary>
public sealed class CapabilityGateOpenApiDocumentTransformerTests
{
    private const string GatedDescriptorId = "test.experimental.capability";
    private const string GatedPath = "/gated/ping";
    private const string OpenPath = "/open/ping";

    private static readonly CapabilityDescriptor ExperimentalDescriptor = new()
    {
        Id = GatedDescriptorId,
        Category = "test",
        Kind = CapabilityKind.Feature,
        Maturity = CapabilityMaturity.Experimental,
    };

    [Fact]
    public async Task Transform_WhenGatedCapabilityDisabled_DropsOperationFromDocument()
    {
        using var server = BuildServer(experimentalEnabled: false);
        using var client = server.CreateClient();

        var paths = await GetDocumentPathsAsync(client);

        paths.Should().Contain(OpenPath, "ungated operations are always described");
        paths.Should().NotContain(GatedPath, "a disabled-experimental operation must not appear in the document");
    }

    [Fact]
    public async Task Transform_WhenGatedCapabilityEnabled_KeepsOperationInDocument()
    {
        using var server = BuildServer(experimentalEnabled: true);
        using var client = server.CreateClient();

        var paths = await GetDocumentPathsAsync(client);

        paths.Should().Contain(OpenPath);
        paths.Should().Contain(GatedPath, "an enabled experimental operation is described normally");
    }

    private static async Task<IReadOnlyCollection<string>> GetDocumentPathsAsync(HttpClient client)
    {
        var json = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        using var document = JsonDocument.Parse(json);

        return document.RootElement.TryGetProperty("paths", out var pathsElement)
            ? pathsElement.EnumerateObject().Select(property => property.Name).ToArray()
            : [];
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
                        services.AddCapabilityGatedOpenApi();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapOpenApi();

                            var gated = endpoints.MapGroup("/gated")
                                .WithCapabilityGate(GatedDescriptorId);
                            gated.MapGet("/ping", () => Results.Ok("pong"));

                            endpoints.MapGet(OpenPath, () => Results.Ok("pong"));
                        });
                    });
            });

        var host = builder.Build();
        host.Start();
        return host.GetTestServer();
    }

    /// <summary>
    /// Minimal <see cref="ICapabilityRegistry"/> exposing a single experimental
    /// descriptor and deferring resolution to the shared
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
