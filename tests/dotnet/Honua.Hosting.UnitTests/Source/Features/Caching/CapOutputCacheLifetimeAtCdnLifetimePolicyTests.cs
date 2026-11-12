// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.TestQuality)]
public sealed class CapOutputCacheLifetimeAtCdnLifetimePolicyTests
{
    [UnitTheory]
    [Operation(Operations.Cache)]
    [InlineData(nameof(ResponseCacheClass.Tile), 3600, 300)]
    [InlineData(nameof(ResponseCacheClass.MetadataDocument), 600, 60)]
    [InlineData(nameof(ResponseCacheClass.StaticObject), 120, 120)]
    public async Task ServeResponseAsync_DeclaredClass_NeverOutlivesCdnLifetime(string cacheClass, int configuredSeconds, int expectedSeconds)
    {
        var context = Request(Enum.Parse<ResponseCacheClass>(cacheClass), new SharedCacheOptions());
        context.ResponseExpirationTimeSpan = TimeSpan.FromSeconds(configuredSeconds);

        await new CapOutputCacheLifetimeAtCdnLifetimePolicy().ServeResponseAsync(context, CancellationToken.None);

        context.ResponseExpirationTimeSpan.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
        context.AllowCacheStorage.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_LayerLifetime_CapsAtThatLayersCdnLifetime()
    {
        var options = new SharedCacheOptions();
        options.Layers["parcels/0"] = new SharedCacheLifetime { MaxAgeSeconds = 10, SharedMaxAgeSeconds = 20 };
        var context = Request(ResponseCacheClass.Tile, options);
        context.HttpContext.Request.RouteValues["serviceId"] = "parcels";
        context.HttpContext.Request.RouteValues["layerId"] = "0";
        context.ResponseExpirationTimeSpan = TimeSpan.FromHours(1);

        await new CapOutputCacheLifetimeAtCdnLifetimePolicy().ServeResponseAsync(context, CancellationToken.None);

        context.ResponseExpirationTimeSpan.Should().Be(TimeSpan.FromSeconds(20));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_ZeroCdnLifetime_DoesNotStore()
    {
        var options = new SharedCacheOptions { Tile = new SharedCacheLifetime { SharedMaxAgeSeconds = 0 } };
        var context = Request(ResponseCacheClass.Tile, options);

        await new CapOutputCacheLifetimeAtCdnLifetimePolicy().ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_NoDeclaredClass_LeavesLifetime()
    {
        var context = new OutputCacheContext
        {
            HttpContext = new DefaultHttpContext(),
            AllowCacheStorage = true,
            ResponseExpirationTimeSpan = TimeSpan.FromHours(12),
        };

        await new CapOutputCacheLifetimeAtCdnLifetimePolicy().ServeResponseAsync(context, CancellationToken.None);

        context.ResponseExpirationTimeSpan.Should().Be(TimeSpan.FromHours(12));
    }

    private static OutputCacheContext Request(ResponseCacheClass cacheClass, SharedCacheOptions options)
    {
        var services = new ServiceCollection()
            .AddSingleton<IOptionsMonitor<SharedCacheOptions>>(new StaticOptionsMonitor(options))
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.SetEndpoint(new Endpoint(
            null, new EndpointMetadataCollection(new SharedCacheClassMetadata(cacheClass)), "cached"));
        return new OutputCacheContext { HttpContext = httpContext, AllowCacheStorage = true };
    }

    private sealed class StaticOptionsMonitor(SharedCacheOptions value) : IOptionsMonitor<SharedCacheOptions>
    {
        public SharedCacheOptions CurrentValue => value;

        public SharedCacheOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<SharedCacheOptions, string?> listener) => null;
    }
}
