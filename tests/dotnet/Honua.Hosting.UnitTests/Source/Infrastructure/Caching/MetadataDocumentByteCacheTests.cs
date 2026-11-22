// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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

    private sealed class SteppingTimeProvider : TimeProvider
    {
        private long _ticks = TimeSpan.TicksPerSecond * 1000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        internal void Advance(TimeSpan delta) => _ticks += delta.Ticks;
    }
}
