// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.Cache)]
public sealed class SharedCacheLifetimeMiddlewareTests
{
    [UnitTheory]
    [InlineData("default", 300)]
    [InlineData("layer", 20)]
    [InlineData("handler", 12)]
    [InlineData("emergency", 15)]
    [InlineData("shorter", 5)]
    [InlineData("zero", 0)]
    public async Task ReturnedResponse_StoresForSelectedLifetime_AndRefreshesAfterExpiry(string selection, int expectedSeconds)
    {
        await using var application = new LifetimeApplication(selection,
            (_, body) => Task.FromResult<IResult>(Results.Text(body)));
        await application.AssertLifetimeAsync(expectedSeconds);
    }

    [UnitTheory]
    [InlineData("default", 300)]
    [InlineData("layer", 20)]
    [InlineData("handler", 12)]
    [InlineData("emergency", 15)]
    [InlineData("shorter", 5)]
    [InlineData("zero", 0)]
    public async Task StreamedResponse_StoresForSelectedLifetime_AndRefreshesAfterExpiry(string selection, int expectedSeconds)
    {
        await using var application = new LifetimeApplication(selection, WriteBodyAsync);
        await application.AssertLifetimeAsync(expectedSeconds);
    }

    private static async Task<IResult> WriteBodyAsync(HttpContext context, string body)
    {
        SharedCacheResponse.Apply(context);
        await context.Response.WriteAsync(body);
        return Results.Empty;
    }

    private sealed class LifetimeApplication : IAsyncDisposable
    {
        private readonly string _selection;
        private readonly Func<HttpContext, string, Task<IResult>> _writeBody;
        private readonly ManualClock _clock = new();
        private readonly RecordingStore _store;
        private readonly WebApplication _application;
        private int _executions;

        internal LifetimeApplication(string selection, Func<HttpContext, string, Task<IResult>> writeBody)
        {
            _selection = selection;
            _writeBody = writeBody;
            _store = new RecordingStore(_clock);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            ConfigureCache(builder, selection);
            builder.Services.AddSingleton<IOutputCacheStore>(_store);
            _application = builder.Build();
            _application.UseOutputCache();
            _application.MapGet("/tiles/{serviceId}/{layerId}", (Func<HttpContext, Task<IResult>>)HandleRequestAsync)
                .WithSharedCacheClass(ResponseCacheClass.Tile).CacheOutput("Tile");
        }

        internal async Task AssertLifetimeAsync(int expectedSeconds)
        {
            await _application.StartAsync();
            using var client = _application.GetTestClient();
            (await client.GetStringAsync("/tiles/parcels/0")).Should().Be("1");
            if (expectedSeconds == 0)
            {
                _store.Lifetimes.Should().BeEmpty();
                (await client.GetStringAsync("/tiles/parcels/0")).Should().Be("2");
                return;
            }

            _store.Lifetimes.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
            (await client.GetStringAsync("/tiles/parcels/0")).Should().Be("1");
            _executions.Should().Be(1);
            _clock.Advance(TimeSpan.FromSeconds(expectedSeconds + 1));
            (await client.GetStringAsync("/tiles/parcels/0")).Should().Be("2");
            _executions.Should().Be(2);
        }

        public ValueTask DisposeAsync() => _application.DisposeAsync();

        private Task<IResult> HandleRequestAsync(HttpContext context)
        {
            ResponseAccessVerdict.Record(context, AccessDecision.Allowed());
            if (_selection is "handler" or "zero")
            {
                SharedCacheResponse.UseLifetime(context,
                    SharedCacheLifetime.ForBrowserAndCdn(_selection == "zero" ? 0 : 12));
            }
            var body = Interlocked.Increment(ref _executions).ToString(CultureInfo.InvariantCulture);
            return _writeBody(context, body);
        }

        private static void ConfigureCache(WebApplicationBuilder builder, string selection)
        {
            builder.Services.Configure<SharedCacheOptions>(options =>
            {
                if (selection == "layer")
                {
                    options.Layers["parcels/0"] = SharedCacheLifetime.ForBrowserAndCdn(20);
                }
                options.Emergency.AllPublicLayers = selection == "emergency";
            });
            builder.Services.AddOutputCache(options =>
            {
                options.AddBasePolicy(policy => policy.AddPolicy<CapOutputCacheLifetimeAtCdnLifetimePolicy>());
                options.AddPolicy("Tile", policy => policy.Expire(selection == "shorter"
                    ? TimeSpan.FromSeconds(5) : new OutputCacheTtlOptions().MvtTile));
            });
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class RecordingStore(ManualClock clock) : IOutputCacheStore
    {
        private readonly ConcurrentDictionary<string, (byte[] Value, DateTimeOffset Expires)> _entries = new();
        internal List<TimeSpan> Lifetimes { get; } = [];

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
            => ValueTask.FromResult(_entries.TryGetValue(key, out var entry) && entry.Expires > clock.GetUtcNow()
                ? entry.Value : null);

        public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken)
        {
            Lifetimes.Add(validFor);
            _entries[key] = (value, clock.GetUtcNow() + validFor);
            return ValueTask.CompletedTask;
        }

        public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
        {
            _entries.Clear();
            return ValueTask.CompletedTask;
        }
    }
}
