// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Tests.Features.Caching;

/// <summary>
/// Behaviour guard for the shared output-cache base policy (SEC-20).
/// </summary>
/// <remarks>
/// <para>
/// The base policy applies to every matched endpoint, so anything it enables is
/// enabled for the whole surface. Three properties are asserted here:
/// </para>
/// <list type="number">
///   <item><description>Caching is opt-in: a route that never called <c>CacheOutput</c> is not stored.</description></item>
///   <item><description>A request carrying an application-defined credential (embed key, API key, portal/scene token) neither populates nor reads the shared cache, whatever named policy the route opted into.</description></item>
///   <item><description>A response the handler marked <c>Cache-Control: no-store</c> is not stored, on every named policy rather than the three that opted in individually.</description></item>
/// </list>
/// <para>
/// Output caching is entitlement-gated in <c>Program</c>, so these tests drive the
/// policy composition directly through a host that always wires
/// <c>UseOutputCache()</c> — the enabled path.
/// </para>
/// </remarks>
[Trait("Tier", "Fast")]
[Trait("Category", "Unit")]
public sealed class BaseOutputCachePolicyTests
{
    private const string Path = "/ogc/features";

    [Fact]
    public async Task RouteWithoutACachePolicy_IsNotServedFromTheSharedCache()
    {
        var state = new EndpointState();
        using var host = await StartHostAsync(state, cachePolicyName: null);
        using var client = host.GetTestClient();

        (await client.GetStringAsync(Path)).Should().Be("1");
        (await client.GetStringAsync(Path)).Should().Be("2");

        state.Invocations.Should().Be(
            2,
            "the base policy must not cache a route that never opted into an output-cache policy");
    }

    [Fact]
    public async Task RouteWithANamedCachePolicy_IsStillServedFromTheSharedCache()
    {
        var state = new EndpointState();
        using var host = await StartHostAsync(state, cachePolicyName: "OgcLandingPage");
        using var client = host.GetTestClient();

        (await client.GetStringAsync(Path)).Should().Be("1");
        (await client.GetStringAsync(Path)).Should().Be("1");

        state.Invocations.Should().Be(
            1,
            "a genuinely public, URL-pure metadata response must keep its caching");
    }

    [Theory]
    [InlineData("X-Honua-Embed-Key")]
    [InlineData("X-API-Key")]
    [InlineData("X-Esri-Authorization")]
    [InlineData("X-Honua-Token")]
    [InlineData("Authorization")]
    public async Task RequestCarryingACredentialHeader_DoesNotPopulateTheSharedCache(string header)
    {
        var state = new EndpointState();
        using var host = await StartHostAsync(state, cachePolicyName: "OgcLandingPage");
        using var client = host.GetTestClient();

        (await SendAsync(client, header, "first")).Should().Be("1");
        (await SendAsync(client, header, "second")).Should().Be("2");

        state.Invocations.Should().Be(
            2,
            $"a response produced for a caller presenting {header} is not a function of the request URL the cache keys on");
    }

    [Theory]
    [InlineData("token")]
    [InlineData("key")]
    public async Task RequestCarryingACredentialQueryParameter_DoesNotPopulateTheSharedCache(string parameter)
    {
        var state = new EndpointState();
        using var host = await StartHostAsync(state, cachePolicyName: "OgcLandingPage");
        using var client = host.GetTestClient();

        (await client.GetStringAsync($"{Path}?{parameter}=abc")).Should().Be("1");
        (await client.GetStringAsync($"{Path}?{parameter}=abc")).Should().Be("2");

        state.Invocations.Should().Be(
            2,
            $"?{parameter}= carries a caller-bound credential, so the response must not be replayed to the next caller of the same URL");
    }

    [Fact]
    public async Task CredentialedRequest_IsNotServedAnEntryStoredForAnAnonymousCaller()
    {
        var state = new EndpointState();
        using var host = await StartHostAsync(state, cachePolicyName: "OgcLandingPage");
        using var client = host.GetTestClient();

        (await client.GetStringAsync(Path)).Should().Be("1");

        (await SendAsync(client, "X-Honua-Embed-Key", "first")).Should().Be(
            "2",
            "a credential presenter must reach the handler so the key, origin, rate accounting and revocation checks run");
    }

    [Fact]
    public async Task ResponseMarkedNoStore_IsNotServedFromTheSharedCache()
    {
        var state = new EndpointState { NoStore = true };
        using var host = await StartHostAsync(state, cachePolicyName: "OgcLandingPage");
        using var client = host.GetTestClient();

        (await client.GetStringAsync(Path)).Should().Be("1");
        (await client.GetStringAsync(Path)).Should().Be("2");

        state.Invocations.Should().Be(
            2,
            "the base policy must honour a Cache-Control: no-store the handler set, on every named policy");
    }

    [Fact]
    public async Task RouteDeclaringNoCache_IsNotServedFromTheSharedCache()
    {
        var state = new EndpointState();
        using var host = await StartHostAsync(state, cachePolicyName: null, noCache: true);
        using var client = host.GetTestClient();

        (await client.GetStringAsync(Path)).Should().Be("1");
        (await client.GetStringAsync(Path)).Should().Be("2");

        state.Invocations.Should().Be(2);
    }

    private static async Task<string> SendAsync(HttpClient client, string header, string value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Path);
        request.Headers.TryAddWithoutValidation(header, value);
        using var response = await client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    private static Task<IHost> StartHostAsync(EndpointState state, string? cachePolicyName, bool noCache = false)
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
                    var route = endpoints.MapGet(Path, async (HttpContext context) =>
                    {
                        var invocation = Interlocked.Increment(ref state.Invocations);
                        if (state.NoStore)
                        {
                            context.Response.Headers[HeaderNames.CacheControl] = "no-store";
                        }

                        await context.Response.WriteAsync(
                            invocation.ToString(CultureInfo.InvariantCulture));
                    });

                    if (cachePolicyName is not null)
                    {
                        route.CacheOutput(cachePolicyName);
                    }

                    if (noCache)
                    {
                        route.CacheOutput(static policy => policy.NoCache());
                    }
                });
            })).StartAsync();

    private sealed class EndpointState
    {
        public int Invocations;

        public bool NoStore { get; init; }
    }
}
