// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Authentication;

public sealed class PortalTokenAuthenticationMiddlewareTests
{
    private const string Referer = "https://native-proof.example/";
    private const string TilePath = "/rest/services/test/MapServer/tile/3/2/1";

    [UnitTest]
    public async Task InvokeAsync_OAuthFormToken_DoesNotAttemptPortalAuthentication()
    {
        var nextCalled = false;
        var middleware = new PortalTokenAuthenticationMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            EnabledOptions());
        var context = new DefaultHttpContext();
        context.Request.Path = "/sharing/rest/oauth2/revoke";
        context.Request.ContentType = "application/x-www-form-urlencoded";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.User.Identity?.IsAuthenticated.Should().BeFalse();
    }

    // RFC 6750 §2.3: a response to a request whose credential travelled in the URI query must
    // carry Cache-Control: private, merged with whatever the endpoint set.
    [Theory]
    [InlineData(null, "private")]
    [InlineData("max-age=60", "max-age=60, private")]
    [InlineData("public, max-age=60, s-maxage=600", "max-age=60, private")]
    [InlineData("no-store", "no-store")]
    [InlineData("private, max-age=30", "private, max-age=30")]
    [InlineData("no-cache, no-store, must-revalidate", "no-cache, no-store, must-revalidate")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task InvokeAsync_QueryTokenRequest_MergesCacheControlPrivate(string? endpointPolicy, string expected)
    {
        await using var services = await CreateServicesAsync();
        var context = TrackedContext(services, out var response);
        context.Request.QueryString = new QueryString("?f=json&token=" + Uri.EscapeDataString(services.IssuedToken));

        await RunAsync(context, response, endpointPolicy);

        context.User.Identity!.IsAuthenticated.Should().BeTrue();
        context.Response.Headers.CacheControl.ToString().Should().Be(expected);
    }

    [UnitTest]
    public async Task InvokeAsync_QueryApiKeyRequest_GetsCacheControlPrivate()
    {
        await using var services = await CreateServicesAsync();
        var context = TrackedContext(services, out var response);
        context.Request.QueryString = new QueryString("?f=json&token=" + Uri.EscapeDataString(services.ReadKey));

        await RunAsync(context, response, "public, max-age=3600");

        context.User.Identity!.AuthenticationType.Should().Be(AuthenticationExtensions.ApiKeyScheme);
        context.Response.Headers.CacheControl.ToString().Should().Be("max-age=3600, private");
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-Esri-Authorization")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task InvokeAsync_HeaderTokenRequest_LeavesCacheControlUnchanged(string header)
    {
        await using var services = await CreateServicesAsync();
        var context = TrackedContext(services, out var response);
        context.Request.Headers[header] = "Bearer " + services.IssuedToken;

        await RunAsync(context, response, "public, max-age=60");

        context.User.Identity!.IsAuthenticated.Should().BeTrue();
        context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=60",
            "only URI-carried credentials need the RFC 6750 directive here; the shared credentialed-response policy owns the rest");
    }

    [UnitTest]
    public async Task InvokeAsync_AnonymousRequest_LeavesCacheControlUnchanged()
    {
        await using var services = await CreateServicesAsync();
        var context = TrackedContext(services, out var response);
        context.Request.QueryString = new QueryString("?f=json");

        await RunAsync(context, response, "public, max-age=60");

        context.User.Identity?.IsAuthenticated.Should().BeFalse();
        context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=60");
    }

    [UnitTest]
    public async Task InvokeAsync_InvalidQueryToken_DoesNotRegisterThePrivateDirective()
    {
        await using var services = await CreateServicesAsync();
        var context = TrackedContext(services, out var response);
        context.Request.QueryString = new QueryString("?f=json&token=not-a-token");

        await RunAsync(context, response, "public, max-age=60");

        context.User.Identity?.IsAuthenticated.Should().BeFalse();
        context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=60");
    }

    private static async Task RunAsync(HttpContext context, OnStartingResponseFeature response, string? endpointPolicy)
    {
        var middleware = new PortalTokenAuthenticationMiddleware(
            ctx =>
            {
                if (endpointPolicy is not null)
                {
                    ctx.Response.Headers.CacheControl = endpointPolicy;
                }

                return Task.CompletedTask;
            },
            EnabledOptions());

        await middleware.InvokeAsync(context);
        await response.StartAsync();
    }

    private static IOptionsMonitor<PortalTokenAuthenticationOptions> EnabledOptions()
    {
        var options = Substitute.For<IOptionsMonitor<PortalTokenAuthenticationOptions>>();
        options.CurrentValue.Returns(new PortalTokenAuthenticationOptions { Enabled = true });
        return options;
    }

    private static DefaultHttpContext TrackedContext(TestServices services, out OnStartingResponseFeature response)
    {
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature());
        response = new OnStartingResponseFeature();
        features.Set<IHttpResponseFeature>(response);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(Stream.Null));
        var context = new DefaultHttpContext(features) { RequestServices = services.Provider };
        context.Request.Method = "GET";
        context.Request.Path = TilePath;
        context.Request.Headers.Referer = Referer;
        return context;
    }

    private static async Task<TestServices> CreateServicesAsync()
    {
        var store = new InMemoryAdminApiKeyStore();
        var readKey = await store.CreateAsync("tile-reader", ["read:test"],
            DateTimeOffset.UtcNow.AddMinutes(10), "test", CancellationToken.None);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IPortalTokenIssuer, PortalTokenIssuer>();
        services.AddSingleton(new ApiKeyAuthenticationDependencies(
            Options.Create(new ApiKeyAuthenticationOptions { EnvironmentName = "Production" }),
            secretResolver: null,
            adminApiKeyStore: store));
        services.AddAuthentication(PortalTokenAuthenticationExtensions.PortalTokenScheme)
            .AddScheme<AuthenticationSchemeOptions, PortalTokenAuthenticationHandler>(
                PortalTokenAuthenticationExtensions.PortalTokenScheme, _ => { });
        var provider = services.BuildServiceProvider();
        var issued = await provider.GetRequiredService<IPortalTokenIssuer>().IssueAsync(new PortalTokenIssueRequest(
            "tile-viewer", "Tile Viewer", null, ["reader"], PortalTokenClientType.Referer,
            Referer, DateTimeOffset.UtcNow.AddMinutes(2)), CancellationToken.None);
        return new TestServices(provider, issued.Token, readKey.Key);
    }

    private sealed record TestServices(ServiceProvider Provider, string IssuedToken, string ReadKey) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    /// <summary>Response feature that runs OnStarting callbacks in server (LIFO) order.</summary>
    private sealed class OnStartingResponseFeature : HttpResponseFeature
    {
        private readonly Stack<(Func<object, Task> Callback, object State)> _callbacks = new();

        public override void OnStarting(Func<object, Task> callback, object state) => _callbacks.Push((callback, state));

        public async Task StartAsync()
        {
            while (_callbacks.TryPop(out var entry))
            {
                await entry.Callback(entry.State);
            }

            HasStartedValue = true;
        }

        private bool HasStartedValue { get; set; }

        public override bool HasStarted => HasStartedValue;
    }
}
