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
        cache.Set(1, "wmts-key", doc);

        Assert.Same(doc, cache.Get(1, "wmts-key"));
        Assert.Null(cache.Get(2, "wmts-key"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void Get_WhenExplicitlyInvalidated_DropsPriorEntriesForSameGeneration()
    {
        var cache = new MetadataDocumentByteCache();
        var doc = MetadataDocumentByteCache.CreateDocument("<caps/>"u8, "application/xml", new ETagService());
        cache.Set(5, "wfs-key", doc);

        cache.Invalidate();

        Assert.Null(cache.Get(5, "wfs-key"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task CachedUtf8ContentResult_ServesExactBytesAndConditionalNotModified()
    {
        var payload = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Capabilities/>");
        var etagService = new ETagService();
        var doc = MetadataDocumentByteCache.CreateDocument(payload, "application/xml; charset=utf-8", etagService);

        using var services = new ServiceCollection()
            .AddSingleton<IETagService>(etagService)
            .BuildServiceProvider();

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
}
