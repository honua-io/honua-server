// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Security;

// Walks every route the host registers; nothing here names a route by hand.
public sealed partial class SharedCacheRouteTableTests : IDisposable
{
    private const string HostileCacheControl = "public, max-age=31536000, s-maxage=31536000, immutable";

    private static readonly string[] WriteMethods =
        [HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete];

    private static readonly (string Name, Action<HttpContext> Present)[] Credentials =
    [
        ("Authorization header", context => context.Request.Headers.Authorization = "Bearer credential"),
        ("X-API-Key header", context => context.Request.Headers["X-API-Key"] = "credential"),
        ("X-Esri-Authorization header", context => context.Request.Headers["X-Esri-Authorization"] = "Bearer credential"),
        ("X-Honua-Embed-Key header", context => context.Request.Headers["X-Honua-Embed-Key"] = "credential"),
        ("X-Honua-Token header", context => context.Request.Headers["X-Honua-Token"] = "credential"),
        ("token query parameter", context => context.Request.QueryString = new QueryString("?token=credential")),
        ("key query parameter", context => context.Request.QueryString = new QueryString("?key=credential")),
        ("admin session cookie", context => context.Request.Headers.Cookie = $"{AdminAuthSessionStore.AuthSessionCookieName}=credential"),
        ("tenant header", context => context.Request.Headers["X-Honua-Tenant"] = "tenant-a"),
        ("scoped job header", context => context.Request.Headers["X-Honua-Job-Id"] = "credential"),
        ("forwarded client certificate", context => context.Request.Headers["X-Forwarded-Client-Cert"] = "credential"),
        ("authenticated principal", context => context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user")], "Bearer"))),
        ("development bypass principal", context => context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("auth_type", "dev-bypass")], "dev-bypass"))),
        ("claims without authentication", context => context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant", "a")]))),
    ];

    private const string EventModeOnlyRoute = "/internal/control-plane/scheduled-tick";

    private readonly TestWebApplicationFactory _pollModeHost = new();
    private readonly WebApplicationFactory<Program> _eventModeHost;

    public SharedCacheRouteTableTests()
        => _eventModeHost = new TestWebApplicationFactory()
            .WithWebHostBuilder(builder => builder.UseSetting("ControlPlane:TriggerMode", "Event"));

    [UnitTest]
    public void RouteTable_CoversTheEventModeHostAsWellAsTheDefaultHost()
        => Routes().Select(route => route.Pattern).Should().Contain(EventModeOnlyRoute);

    [UnitTest]
    public void RoutesDeclaringAPublicCacheClass_AreAnonymousReadRoutesOutsideAdmin()
    {
        var declared = Routes().Where(route => route.CacheClass is not null).ToArray();

        declared.Should().NotBeEmpty("tiles, feature queries and metadata documents declare a class");
        declared.Where(route => IsAdmin(route.Pattern)).Select(Describe).Should().BeEmpty(
            "an admin route is never shared-cacheable");
        declared.Where(route => RequiresAuthorization(route.Endpoint)).Select(Describe).Should().BeEmpty(
            "a route that requires authorization is never shared-cacheable");
        declared.Where(route => !route.Methods.Any(HttpMethods.IsGet)).Select(Describe).Should().BeEmpty(
            "only a read can be shared-cacheable");
    }

    [UnitTest]
    public void EveryRoute_WithAnyCredential_IsNeverSharedCacheable()
    {
        var failures = new List<string>();
        foreach (var route in Routes())
        {
            foreach (var method in route.Methods)
            {
                foreach (var (name, present) in Credentials)
                {
                    var context = HostileResponse(route, method);
                    present(context);

                    SharedCacheGuard.Apply(context);

                    if (IsSharedCacheStorable(context.Response))
                    {
                        failures.Add($"{method} {route.Pattern} with {name}");
                    }
                }
            }
        }

        failures.Should().BeEmpty();
    }

    [UnitTest]
    public void EveryRoute_AnonymousRequest_IsSharedCacheableOnlyAsAReadOnADeclaredRoute()
    {
        var failures = new List<string>();
        var sharedReads = 0;
        foreach (var route in Routes())
        {
            foreach (var method in route.Methods)
            {
                var context = HostileResponse(route, method);

                SharedCacheGuard.Apply(context);

                var mayBeShared = route.CacheClass is not null && (HttpMethods.IsGet(method) || HttpMethods.IsHead(method));
                if (IsSharedCacheStorable(context.Response) != mayBeShared)
                {
                    failures.Add($"{method} {route.Pattern}");
                }

                sharedReads += mayBeShared ? 1 : 0;
            }
        }

        failures.Should().BeEmpty();
        sharedReads.Should().BeGreaterThan(0);
    }

    [UnitTest]
    public void EveryRoute_AccessDeniedOrFailed_IsNeverSharedCacheable()
    {
        var failures = new List<string>();
        foreach (var route in Routes())
        {
            foreach (var method in route.Methods)
            {
                var denied = HostileResponse(route, method);
                ResponseAccessVerdict.Record(denied, AccessDecision.RequiresAuth());
                SharedCacheGuard.Apply(denied);

                var failed = HostileResponse(route, method);
                failed.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                SharedCacheGuard.Apply(failed);

                if (IsSharedCacheStorable(denied.Response) || IsSharedCacheStorable(failed.Response))
                {
                    failures.Add($"{method} {route.Pattern}");
                }
            }
        }

        failures.Should().BeEmpty();
    }

    [UnitTest]
    public async Task EveryRoute_ThroughTheRealPipeline_SharesOnlyAnonymousReadsOfDeclaredRoutes()
    {
        using var client = _pollModeHost.CreateClient();
        var failures = new List<string>();
        foreach (var route in Routes())
        {
            foreach (var method in route.Methods)
            {
                foreach (var withApiKey in new[] { false, true })
                {
                    var cacheControl = await SendAsync(client, route, method, withApiKey);
                    var storable = IsSharedCacheStorable(cacheControl);
                    var mayBeShared = !withApiKey
                        && route.CacheClass is not null
                        && (HttpMethods.IsGet(method) || HttpMethods.IsHead(method));
                    if (storable && !mayBeShared)
                    {
                        failures.Add($"{method} {route.Pattern} apiKey={withApiKey}: '{cacheControl}'");
                    }
                }
            }
        }

        failures.Should().BeEmpty();
    }

    public void Dispose()
    {
        _pollModeHost.Dispose();
        _eventModeHost.Dispose();
    }

    private static async Task<string?> SendAsync(HttpClient client, Route route, string method, bool withApiKey)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), Concrete(route.Pattern));
        if (withApiKey)
        {
            request.Headers.Add("X-API-Key", "not-a-real-key");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        return response.Headers.TryGetValues("Cache-Control", out var values) ? string.Join(", ", values) : null;
    }

    private static string Concrete(string pattern)
        => RouteParameter().Replace(pattern, match => match.Groups["constraint"].Value.Contains("int", StringComparison.Ordinal)
            || match.Groups["constraint"].Value.Contains("apiVersion", StringComparison.Ordinal)
                ? "1"
                : "x");

    private static DefaultHttpContext HostileResponse(Route route, string method)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Request.Method = method;
        context.SetEndpoint(route.Endpoint);
        context.Response.Headers.CacheControl = HostileCacheControl;
        return context;
    }

    private static bool IsSharedCacheStorable(HttpResponse response)
        => IsSharedCacheStorable(response.Headers.CacheControl.ToString());

    // A response with no Cache-Control may be stored by a shared cache on heuristics.
    private static bool IsSharedCacheStorable(string? cacheControl)
        => string.IsNullOrEmpty(cacheControl)
           || (!cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase)
               && !cacheControl.Contains("private", StringComparison.OrdinalIgnoreCase));

    private static bool IsAdmin(string pattern)
        => pattern.Contains("/admin", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresAuthorization(Endpoint endpoint)
        => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null
           && (endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0
               || endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>().Count > 0);

    private static string Describe(Route route) => $"{string.Join('|', route.Methods)} {route.Pattern}";

    private Route[] Routes()
        => [.. RoutesOf(_pollModeHost).Concat(RoutesOf(_eventModeHost)).DistinctBy(Describe)];

    private static IEnumerable<Route> RoutesOf(WebApplicationFactory<Program> host)
    {
        using var _ = host.CreateClient();
        return host.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => !string.IsNullOrEmpty(endpoint.RoutePattern.RawText))
            .Select(endpoint => new Route(
                endpoint,
                "/" + endpoint.RoutePattern.RawText!.TrimStart('/'),
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods
                    ? [.. methods]
                    : [HttpMethods.Get, .. WriteMethods],
                endpoint.Metadata.GetMetadata<SharedCacheClassMetadata>()?.CacheClass))
            .ToArray();
    }

    [GeneratedRegex(@"\{\*{0,2}(?<name>[^{}:?=]+)(?<constraint>[^{}]*)\}")]
    private static partial Regex RouteParameter();

    private sealed record Route(RouteEndpoint Endpoint, string Pattern, string[] Methods, ResponseCacheClass? CacheClass);
}
