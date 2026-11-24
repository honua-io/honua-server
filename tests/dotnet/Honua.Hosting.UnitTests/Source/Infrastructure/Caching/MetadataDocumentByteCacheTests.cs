// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Caching;

[Protocol(TestProtocols.TestQuality)]
public sealed class MetadataDocumentByteCacheTests
{
    [UnitTest]
    [Operation(Operations.Cache)]
    public void Get_WhenConfigGenerationAdvances_InvalidatesPriorEntries()
    {
        var cache = new MetadataDocumentByteCache();
        var doc = MetadataDocumentByteCache.CreateDocument("<caps/>"u8, "application/xml", new ETagService());
        var stamp1 = cache.CaptureStamp(1);
        cache.Set(stamp1, "wmts-key", doc);

        Assert.Same(doc, cache.Get(stamp1, "wmts-key"));
        Assert.Null(cache.Get(cache.CaptureStamp(2), "wmts-key"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void Set_WhenOlderGenerationArrivesAfterNewerGeneration_DoesNotRegressOrPoisonCache()
    {
        var cache = new MetadataDocumentByteCache();
        var staleDoc = MetadataDocumentByteCache.CreateDocument("<stale/>"u8, "application/xml", new ETagService());
        var freshDoc = MetadataDocumentByteCache.CreateDocument("<fresh/>"u8, "application/xml", new ETagService());
        var olderStamp = cache.CaptureStamp(10);
        var newerStamp = cache.CaptureStamp(11);

        cache.Set(newerStamp, "wmts-key", freshDoc);
        cache.Set(olderStamp, "wmts-key", staleDoc);

        Assert.Same(freshDoc, cache.Get(newerStamp, "wmts-key"));
        Assert.Null(cache.Get(olderStamp, "wmts-key"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void Set_WhenInvalidatedAfterStampCapture_DoesNotCacheStaleDocument()
    {
        var cache = new MetadataDocumentByteCache();
        var staleDoc = MetadataDocumentByteCache.CreateDocument("<stale/>"u8, "application/xml", new ETagService());
        var preInvalidationStamp = cache.CaptureStamp(5);

        cache.Invalidate();
        cache.Set(preInvalidationStamp, "wfs-key", staleDoc);

        Assert.Null(cache.Get(cache.CaptureStamp(5), "wfs-key"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void Get_WhenExplicitlyInvalidated_DropsPriorEntriesForSameGeneration()
    {
        var cache = new MetadataDocumentByteCache();
        var doc = MetadataDocumentByteCache.CreateDocument("<caps/>"u8, "application/xml", new ETagService());
        var stamp = cache.CaptureStamp(5);
        cache.Set(stamp, "wfs-key", doc);

        cache.Invalidate();

        Assert.Null(cache.Get(cache.CaptureStamp(5), "wfs-key"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void Get_WhenEntryExpiresPastTtl_EvictsAndReturnsNull()
    {
        var clock = new SteppingTimeProvider();
        var cache = new MetadataDocumentByteCache(clock, TimeSpan.FromSeconds(30));
        var doc = MetadataDocumentByteCache.CreateDocument("<caps/>"u8, "application/xml", new ETagService());
        var stamp = cache.CaptureStamp(1);
        cache.Set(stamp, "wms-key", doc);

        Assert.Same(doc, cache.Get(stamp, "wms-key"));
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(cache.Get(stamp, "wms-key"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void Set_WhenBucketFullOfExpiredEntries_SweepsAndStoresNewKey()
    {
        var clock = new SteppingTimeProvider();
        var cache = new MetadataDocumentByteCache(clock, TimeSpan.FromSeconds(30));
        var doc = MetadataDocumentByteCache.CreateDocument("<caps/>"u8, "application/xml", new ETagService());
        var stamp = cache.CaptureStamp(1);
        for (var i = 0; i < 256; i++)
        {
            cache.Set(stamp, "key-" + i, doc);
        }

        cache.Set(stamp, "refused-while-fresh", doc);
        clock.Advance(TimeSpan.FromSeconds(31));
        cache.Set(stamp, "stored-after-sweep", doc);

        Assert.Null(cache.Get(stamp, "refused-while-fresh"));
        Assert.Same(doc, cache.Get(stamp, "stored-after-sweep"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void HasQueryToken_MatchesCaseInsensitively()
    {
        var lower = new DefaultHttpContext();
        lower.Request.QueryString = new QueryString("?SERVICE=WMTS&token=secret");
        var upper = new DefaultHttpContext();
        upper.Request.QueryString = new QueryString("?SERVICE=WMTS&TOKEN=secret");
        var none = new DefaultHttpContext();
        none.Request.QueryString = new QueryString("?SERVICE=WMTS&REQUEST=GetCapabilities");

        Assert.True(MetadataDocumentByteCache.HasQueryToken(lower.Request));
        Assert.True(MetadataDocumentByteCache.HasQueryToken(upper.Request));
        Assert.False(MetadataDocumentByteCache.HasQueryToken(none.Request));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task OutputCacheInvalidationService_InvalidateQueryResponsesAsync_InvalidatesMetadataDocumentCache()
    {
        var cache = new MetadataDocumentByteCache();
        var doc = MetadataDocumentByteCache.CreateDocument("<caps/>"u8, "application/xml", new ETagService());
        var stamp = cache.CaptureStamp(3);
        cache.Set(stamp, "wms-key", doc);

        using var services = new ServiceCollection()
            .AddSingleton(cache)
            .BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        var invalidator = new OutputCacheInvalidationService(
            cacheStore: null,
            responseCache: null,
            metadataCache: null,
            scopeFactory: scopeFactory,
            refreshCoordinator: null,
            logger: NullLogger<OutputCacheInvalidationService>.Instance);

        await invalidator.InvalidateQueryResponsesAsync();

        Assert.Null(cache.Get(cache.CaptureStamp(3), "wms-key"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task CachedUtf8ContentResult_ServesExactBytesAndNotModified()
    {
        var payload = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Capabilities/>");
        var etagService = new ETagService();
        var doc = MetadataDocumentByteCache.CreateDocument(payload, "application/xml; charset=utf-8", etagService);
        using var services = new ServiceCollection().AddSingleton<IETagService>(etagService).BuildServiceProvider();

        var firstContext = new DefaultHttpContext { RequestServices = services };
        firstContext.Request.Method = HttpMethods.Get;
        firstContext.Response.Body = new MemoryStream();
        var wrapped = Assert.IsAssignableFrom<IResult>(await SharedResponseETag.ApplyAsync(firstContext, doc.ToResult()));
        await wrapped.ExecuteAsync(firstContext);

        Assert.Equal("application/xml; charset=utf-8", firstContext.Response.ContentType);
        Assert.Equal(payload.Length, firstContext.Response.ContentLength);
        Assert.Equal(payload, ((MemoryStream)firstContext.Response.Body).ToArray());

        var etag = firstContext.Response.Headers.ETag.ToString();
        Assert.False(string.IsNullOrEmpty(etag));

        var secondContext = new DefaultHttpContext { RequestServices = services };
        secondContext.Request.Method = HttpMethods.Get;
        secondContext.Request.Headers.IfNoneMatch = etag;
        var secondWrapped = Assert.IsAssignableFrom<IResult>(await SharedResponseETag.ApplyAsync(secondContext, doc.ToResult()));
        await secondWrapped.ExecuteAsync(secondContext);
        Assert.Equal((int)HttpStatusCode.NotModified, secondContext.Response.StatusCode);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task CachedUtf8ContentResult_HandlesHeadAndIfMatchPreconditionFailed()
    {
        var payload = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Capabilities/>");
        var etagService = new ETagService();
        var doc = MetadataDocumentByteCache.CreateDocument(payload, "application/xml; charset=utf-8", etagService);
        using var services = new ServiceCollection().AddSingleton<IETagService>(etagService).BuildServiceProvider();

        var headContext = new DefaultHttpContext { RequestServices = services };
        headContext.Request.Method = HttpMethods.Head;
        headContext.Response.Body = new MemoryStream();
        var headWrapped = Assert.IsAssignableFrom<IResult>(await SharedResponseETag.ApplyAsync(headContext, doc.ToResult()));
        await headWrapped.ExecuteAsync(headContext);
        Assert.Equal((int)HttpStatusCode.OK, headContext.Response.StatusCode);
        Assert.Equal(payload.Length, headContext.Response.ContentLength);
        Assert.Empty(((MemoryStream)headContext.Response.Body).ToArray());

        var mismatchContext = new DefaultHttpContext { RequestServices = services };
        mismatchContext.Request.Method = HttpMethods.Get;
        mismatchContext.Request.Headers.IfMatch = "\"non-matching\"";
        var mismatchWrapped = Assert.IsAssignableFrom<IResult>(await SharedResponseETag.ApplyAsync(mismatchContext, doc.ToResult()));
        await mismatchWrapped.ExecuteAsync(mismatchContext);
        Assert.Equal((int)HttpStatusCode.PreconditionFailed, mismatchContext.Response.StatusCode);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetOrBuildJsonAsync_CachesSerializedBytesAndInvalidatesOnGenerationAdvance()
    {
        var cache = new MetadataDocumentByteCache();
        var genSource = new MutableGenerationSource(1);
        using var services = new ServiceCollection()
            .AddSingleton(cache)
            .AddSingleton<IConfigurationGenerationSource>(genSource)
            .AddSingleton<IETagService>(new ETagService())
            .BuildServiceProvider();

        var buildCount = 0;
        var (ct1, body1) = await ExecuteCachedJsonAsync(services, "json-key", () => new SampleJsonDoc("v1", ++buildCount));
        var (ct2, body2) = await ExecuteCachedJsonAsync(services, "json-key", () => new SampleJsonDoc("v1", ++buildCount));

        Assert.Equal(1, buildCount);
        Assert.Equal("application/json; charset=utf-8", ct1);
        Assert.Equal(ct1, ct2);
        Assert.Equal(body1, body2);

        genSource.Generation = 2;
        var (_, body3) = await ExecuteCachedJsonAsync(services, "json-key", () => new SampleJsonDoc("v2", ++buildCount));

        Assert.Equal(2, buildCount);
        Assert.Contains("\"v2\"", Encoding.UTF8.GetString(body3), StringComparison.Ordinal);
    }

    private static async Task<(string? ContentType, byte[] Body)> ExecuteCachedJsonAsync(
        IServiceProvider services,
        string cacheKey,
        Func<SampleJsonDoc> build)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        var result = await MetadataDocumentByteCache.GetOrBuildJsonAsync(context, new JsonMetadataCacheRequest<SampleJsonDoc>
        {
            CacheKey = cacheKey,
            TypeInfo = SampleJsonDocContext.Default.SampleJsonDoc,
            BuildAsync = () => Task.FromResult(build())
        });
        await result.ExecuteAsync(context);
        return (context.Response.ContentType, ((MemoryStream)context.Response.Body).ToArray());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetOrBuildJsonAsync_WhenQueryTokenPresent_BypassesCache()
    {
        var cache = new MetadataDocumentByteCache();
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(cache)
            .BuildServiceProvider();

        var buildCount = 0;
        for (var i = 0; i < 2; i++)
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.QueryString = new QueryString("?token=secret");
            context.Response.Body = new MemoryStream();
            var result = await MetadataDocumentByteCache.GetOrBuildJsonAsync(context, new JsonMetadataCacheRequest<SampleJsonDoc>
            {
                CacheKey = "json-key",
                TypeInfo = SampleJsonDocContext.Default.SampleJsonDoc,
                BuildAsync = () => Task.FromResult(new SampleJsonDoc("tok", ++buildCount))
            });
            await result.ExecuteAsync(context);
        }

        Assert.Equal(2, buildCount);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetOrBuildJsonAsync_WhenBuilderMarksNoStore_DoesNotRetainPartialPayload()
    {
        var cache = new MetadataDocumentByteCache();
        using var services = new ServiceCollection().AddLogging().AddSingleton(cache).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        var result = await MetadataDocumentByteCache.GetOrBuildJsonAsync(context, new JsonMetadataCacheRequest<SampleJsonDoc>
        {
            CacheKey = "json-key",
            TypeInfo = SampleJsonDocContext.Default.SampleJsonDoc,
            BuildAsync = () =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Task.FromResult(new SampleJsonDoc("partial", 1));
            }
        });
        await result.ExecuteAsync(context);

        Assert.Contains("partial", Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()), StringComparison.Ordinal);
        Assert.True(context.Response.GetTypedHeaders().CacheControl?.NoStore);
        Assert.Null(cache.Get(cache.CaptureStamp(0), "json-key"));
        var (_, recoveredBody) = await ExecuteCachedJsonAsync(services, "json-key", () => new SampleJsonDoc("complete", 2));
        Assert.Contains("complete", Encoding.UTF8.GetString(recoveredBody), StringComparison.Ordinal);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetOrBuildJsonAsync_PartitionsEntriesAcrossTenantsWithIdenticalCallerKey()
    {
        var cache = new MetadataDocumentByteCache();
        using var tenantAServices = new ServiceCollection()
            .AddSingleton(cache)
            .AddSingleton<ITenantContext>(new StubTenantContext("tenant-a"))
            .BuildServiceProvider();
        using var tenantBServices = new ServiceCollection()
            .AddSingleton(cache)
            .AddSingleton<ITenantContext>(new StubTenantContext("tenant-b"))
            .BuildServiceProvider();

        var (_, bodyA) = await ExecuteCachedJsonAsync(tenantAServices, "shared-key", () => new SampleJsonDoc("tenant-a-data", 1));
        var (_, bodyB) = await ExecuteCachedJsonAsync(tenantBServices, "shared-key", () => new SampleJsonDoc("tenant-b-data", 2));

        Assert.Contains("tenant-a-data", Encoding.UTF8.GetString(bodyA), StringComparison.Ordinal);
        Assert.Contains("tenant-b-data", Encoding.UTF8.GetString(bodyB), StringComparison.Ordinal);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetOrBuildJsonAsync_PartitionsEntriesAcrossSchemasWithIdenticalCallerKey()
    {
        var cache = new MetadataDocumentByteCache();
        var schemaA = new SchemaContext { CurrentSchema = "schema_a" };
        using var servicesA = new ServiceCollection()
            .AddSingleton(cache)
            .AddSingleton<ISchemaContext>(schemaA)
            .BuildServiceProvider();
        var (_, bodyA) = await ExecuteCachedJsonAsync(servicesA, "shared-key", () => new SampleJsonDoc("schema-a-data", 1));

        var schemaB = new SchemaContext { CurrentSchema = "schema_b" };
        using var servicesB = new ServiceCollection()
            .AddSingleton(cache)
            .AddSingleton<ISchemaContext>(schemaB)
            .BuildServiceProvider();
        var (_, bodyB) = await ExecuteCachedJsonAsync(servicesB, "shared-key", () => new SampleJsonDoc("schema-b-data", 2));

        // Reset ambient AsyncLocal so sibling tests do not inherit schema_b.
        schemaB.CurrentSchema = null;

        Assert.Contains("schema-a-data", Encoding.UTF8.GetString(bodyA), StringComparison.Ordinal);
        Assert.Contains("schema-b-data", Encoding.UTF8.GetString(bodyB), StringComparison.Ordinal);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetOrBuildJsonAsync_WhenTestSchemaHeadersActive_PartitionsByHeader()
    {
        var cache = new MetadataDocumentByteCache();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["HONUA_TEST_SCHEMA_HEADERS"] = "true" })
            .Build();
        using var services = new ServiceCollection()
            .AddSingleton(cache)
            .AddSingleton<IConfiguration>(config)
            .BuildServiceProvider();

        async Task<string> FetchAsync(string headerSchema, string label)
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Headers["X-Honua-Test-Schema"] = headerSchema;
            context.Response.Body = new MemoryStream();
            var result = await MetadataDocumentByteCache.GetOrBuildJsonAsync(context, new JsonMetadataCacheRequest<SampleJsonDoc>
            {
                CacheKey = "shared-key",
                TypeInfo = SampleJsonDocContext.Default.SampleJsonDoc,
                BuildAsync = () => Task.FromResult(new SampleJsonDoc(label, 1))
            });
            await result.ExecuteAsync(context);
            return Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        }

        Assert.Contains("test-schema-1", await FetchAsync("test_1", "test-schema-1"), StringComparison.Ordinal);
        Assert.Contains("test-schema-2", await FetchAsync("test_2", "test-schema-2"), StringComparison.Ordinal);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetOrBuildJsonAsync_PartitionsEntriesAcrossLicenseEditions()
    {
        var cache = new MetadataDocumentByteCache();
        var communityLicense = Substitute.For<ILicenseEntitlementService>();
        communityLicense.GetSnapshot().Returns(new LicenseSnapshot(
            HonuaEdition.Community, true, LicenseValidationState.Valid, null, null, null, null, [], new HashSet<string>(), 1, null));
        var enterpriseLicense = Substitute.For<ILicenseEntitlementService>();
        enterpriseLicense.GetSnapshot().Returns(new LicenseSnapshot(
            HonuaEdition.Enterprise, true, LicenseValidationState.Valid, null, null, null, null, [], new HashSet<string>(), 2, null));

        using var communityServices = new ServiceCollection().AddSingleton(cache).AddSingleton(communityLicense).BuildServiceProvider();
        using var enterpriseServices = new ServiceCollection().AddSingleton(cache).AddSingleton(enterpriseLicense).BuildServiceProvider();

        var (_, bodyCommunity) = await ExecuteCachedJsonAsync(communityServices, "shared-key", () => new SampleJsonDoc("community-doc", 1));
        var (_, bodyEnterprise) = await ExecuteCachedJsonAsync(enterpriseServices, "shared-key", () => new SampleJsonDoc("enterprise-doc", 2));

        Assert.Contains("community-doc", Encoding.UTF8.GetString(bodyCommunity), StringComparison.Ordinal);
        Assert.Contains("enterprise-doc", Encoding.UTF8.GetString(bodyEnterprise), StringComparison.Ordinal);
    }

    private sealed class StubTenantContext(string? tenantId) : ITenantContext
    {
        public string? TenantId { get; } = tenantId;

        public TenantContextSource Source { get; } =
            tenantId is null ? TenantContextSource.Anonymous : TenantContextSource.Claim;

        public bool RequireTenantId(out string resolvedTenantId, out string? reason)
        {
            if (string.IsNullOrEmpty(TenantId))
            {
                resolvedTenantId = string.Empty;
                reason = "missing tenant";
                return false;
            }

            resolvedTenantId = TenantId;
            reason = null;
            return true;
        }
    }

    private sealed class MutableGenerationSource(long generation) : IConfigurationGenerationSource
    {
        internal long Generation { get; set; } = generation;

        public Task<long> GetGenerationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Generation);
    }

    private sealed class SteppingTimeProvider : TimeProvider
    {
        private long _ticks = TimeSpan.TicksPerSecond * 1000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        internal void Advance(TimeSpan delta) => _ticks += delta.Ticks;
    }
}

internal sealed record SampleJsonDoc(string Label, int Sequence);

[JsonSerializable(typeof(SampleJsonDoc))]
internal sealed partial class SampleJsonDocContext : JsonSerializerContext;
