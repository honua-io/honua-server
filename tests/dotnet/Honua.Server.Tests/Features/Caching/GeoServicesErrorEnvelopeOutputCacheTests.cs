// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Caching;

/// <summary>
/// Regression for honua-server#4980. GeoServices errors are Esri envelopes
/// (<c>{"error":{"code":N,...}}</c>) sent over HTTP 200, so the framework's status-code rule
/// stored them on every named output-cache policy and replayed a transient fault to every caller
/// of the same key for the policy TTL.
/// </summary>
/// <remarks>
/// Each test drives the real policy composition from
/// <see cref="ObservabilityServiceCollectionExtensions.ConfigureOutputCaching"/> through a host
/// that always wires <c>UseOutputCache()</c> (output caching is entitlement-gated in
/// <c>Program</c>). The endpoint fails once through the shared error formatter, exactly as a
/// GeoServices handler does, and then succeeds; the second response must be the success.
/// </remarks>
[Trait("Tier", "Fast")]
[Trait("Category", "Unit")]
public sealed class GeoServicesErrorEnvelopeOutputCacheTests
{
    private const string ServicePath = "/rest/services/svc/MapServer";
    private const string TilePath = "/rest/services/svc/FeatureServer/0/tiles/0/0/0.pbf";
    private const string ExportImagePath = "/rest/services/1/ImageServer/exportImage";
    private const string SuccessBody = "{\"ok\":true}";

    public enum Failure
    {
        InternalServerError,
        NotImplemented,
        ServiceUnavailable,
        NotFound,
    }

    [Theory]
    [InlineData("ServiceDirectory")]
    [InlineData("ServiceMetadata")]
    [InlineData("LayerMetadata")]
    [InlineData("ImageServerMetadata")]
    [InlineData("MapServerLegend")]
    [InlineData("MapServerTile")]
    [InlineData("MvtTile")]
    [InlineData("H3MvtTile")]
    public async Task ErrorEnvelopeOnANamedPolicyRoute_IsNotServedFromTheOutputCache(string policy)
    {
        var state = new EndpointState { Failure = Failure.InternalServerError };
        var path = policy.Contains("Mvt", StringComparison.Ordinal) ? TilePath : ServicePath;
        using var host = await StartHostAsync(state, path, policy);
        using var client = host.GetTestClient();

        using var failed = await client.GetAsync(path);
        await AssertEnvelopeAsync(failed, StatusCodes.Status500InternalServerError);

        using var recovered = await client.GetAsync(path);
        ((int)recovered.StatusCode).Should().Be(StatusCodes.Status200OK);
        (await recovered.Content.ReadAsStringAsync()).Should().Be(
            SuccessBody,
            $"a GeoServices error envelope on the {policy} policy must not be replayed after the fault clears");
        state.Invocations.Should().Be(2);
    }

    [Theory]
    [InlineData(Failure.InternalServerError, StatusCodes.Status500InternalServerError)]
    [InlineData(Failure.NotImplemented, StatusCodes.Status501NotImplemented)]
    [InlineData(Failure.ServiceUnavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(Failure.NotFound, StatusCodes.Status404NotFound)]
    public async Task ErrorEnvelopeOfAnyCode_IsNotServedFromTheOutputCache(Failure failure, int code)
    {
        var state = new EndpointState { Failure = failure };
        using var host = await StartHostAsync(state, ServicePath, "ServiceMetadata");
        using var client = host.GetTestClient();

        using var failed = await client.GetAsync(ServicePath);
        await AssertEnvelopeAsync(failed, code);

        using var recovered = await client.GetAsync(ServicePath);
        (await recovered.Content.ReadAsStringAsync()).Should().Be(SuccessBody);
        state.Invocations.Should().Be(2);
    }

    [Fact]
    public async Task SuccessfulResponseOnANamedPolicyRoute_IsStillServedFromTheOutputCache()
    {
        var state = new EndpointState { Failure = null };
        using var host = await StartHostAsync(state, ServicePath, "ServiceMetadata");
        using var client = host.GetTestClient();

        (await client.GetStringAsync(ServicePath)).Should().Be(SuccessBody);
        (await client.GetStringAsync(ServicePath)).Should().Be(SuccessBody);

        state.Invocations.Should().Be(1, "only error envelopes lose their caching");
    }

    [Theory]
    [InlineData("MvtTile")]
    [InlineData("H3MvtTile")]
    public async Task OverBudgetTileRefusalEnvelope_IsStillServedFromTheOutputCache(string policy)
    {
        // The H3 and vector-tile handlers refuse an over-budget tile through
        // StandardErrorHelpers.CreatePayloadTooLarge; on a GeoServices route that is a 413
        // envelope over HTTP 200, stored deliberately because the key carries the byte budget.
        var state = new EndpointState { OverBudget = true };
        using var host = await StartHostAsync(state, TilePath, policy);
        using var client = host.GetTestClient();

        using var first = await client.GetAsync(TilePath);
        await AssertEnvelopeAsync(first, StatusCodes.Status413PayloadTooLarge);
        using var second = await client.GetAsync(TilePath);
        await AssertEnvelopeAsync(second, StatusCodes.Status413PayloadTooLarge);

        state.Invocations.Should().Be(
            1,
            "the over-budget refusal is the one error envelope the tile policies deliberately cache");
    }

    [Fact]
    public async Task OverBudgetEnvelopeOnANonTilePolicy_IsNotServedFromTheOutputCache()
    {
        var state = new EndpointState { OverBudget = true };
        using var host = await StartHostAsync(state, ServicePath, "ServiceMetadata");
        using var client = host.GetTestClient();

        using var first = await client.GetAsync(ServicePath);
        await AssertEnvelopeAsync(first, StatusCodes.Status413PayloadTooLarge);
        using var second = await client.GetAsync(ServicePath);

        (await second.Content.ReadAsStringAsync()).Should().Be(
            SuccessBody,
            "only the budget-partitioned tile policies may re-admit a 413 envelope");
    }

    [Fact]
    public async Task ExportImageErrorEnvelope_IsNotServedFromTheOutputCache()
    {
        // GET .../ImageServer/exportImage declares no CacheOutput policy, so only the base
        // policy applies (EndpointResponseMetadataTests
        // .ImageServerExportImageEndpoints_DeclareNoOutputCachePolicy guards the declaration).
        var state = new EndpointState { Failure = Failure.NotImplemented, SuccessIsImage = true };
        using var host = await StartHostAsync(state, ExportImagePath, policy: null);
        using var client = host.GetTestClient();

        using var failed = await client.GetAsync(ExportImagePath);
        await AssertEnvelopeAsync(failed, StatusCodes.Status501NotImplemented);

        using var recovered = await client.GetAsync(ExportImagePath);
        ((int)recovered.StatusCode).Should().Be(StatusCodes.Status200OK);
        recovered.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        state.Invocations.Should().Be(2);
    }

    private static async Task AssertEnvelopeAsync(HttpResponseMessage response, int code)
    {
        ((int)response.StatusCode).Should().Be(StatusCodes.Status200OK, "GeoServices errors use HTTP 200");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(code);
    }

    private static Task<IHost> StartHostAsync(EndpointState state, string path, string? policy)
        => new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton<IOptions<LimitsOptions>>(
                    Options.Create(new LimitsOptions { Tiles = new TileLimits() }));
                ObservabilityServiceCollectionExtensions.ConfigureOutputCaching(
                    services,
                    new ConfigurationBuilder().Build());
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseOutputCache();
                app.UseEndpoints(endpoints =>
                {
                    var route = endpoints.MapGet(path, (HttpContext context) =>
                    {
                        var invocation = Interlocked.Increment(ref state.Invocations);
                        if (state.OverBudget)
                        {
                            return invocation == 1 || (policy?.Contains("Mvt", StringComparison.Ordinal) ?? false)
                                ? StandardErrorHelpers.CreatePayloadTooLarge(context, "Tile exceeds the configured size limit.")
                                : Results.Text(SuccessBody, "application/json");
                        }

                        if (invocation == 1 && state.Failure is { } failure)
                        {
                            return failure switch
                            {
                                Failure.NotImplemented => StandardErrorHelpers.CreateNotImplemented(context, "Provider unavailable."),
                                Failure.ServiceUnavailable => StandardErrorHelpers.CreateServiceUnavailable(context, "Provider unavailable.", retryAfterSeconds: 1),
                                Failure.NotFound => StandardErrorHelpers.CreateNotFound(context, "Layer not found."),
                                _ => StandardErrorHelpers.CreateInternalServerError(context, "Provider failed."),
                            };
                        }

                        return state.SuccessIsImage
                            ? Results.Bytes(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "image/png")
                            : Results.Text(SuccessBody, "application/json");
                    });

                    if (policy is not null)
                    {
                        route.CacheOutput(policy);
                    }
                });
            })).StartAsync();

    private sealed class EndpointState
    {
        public int Invocations;

        public Failure? Failure { get; init; }

        public bool OverBudget { get; init; }

        public bool SuccessIsImage { get; init; }
    }
}
