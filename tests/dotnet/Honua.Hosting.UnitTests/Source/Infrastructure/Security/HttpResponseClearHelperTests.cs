// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Tests.Infrastructure.Security;

public sealed class HttpResponseClearHelperTests
{
    [UnitTest]
    public void ClearPreservingCorsHeaders_PreservesCorsAndVaryHeaders_ClearsOtherBufferedHeaders()
    {
        var context = new DefaultHttpContext();
        context.Response.StatusCode = StatusCodes.Status201Created;
        context.Response.Headers.AccessControlAllowOrigin = "https://client.example";
        context.Response.Headers.AccessControlExposeHeaders = "X-Request-Id";
        context.Response.Headers.Vary = "Origin, Accept-Encoding";
        context.Response.Headers["X-Custom-Buffered"] = "stale-value";
        context.Response.Headers.ETag = "\"abc123\"";

        HttpResponseClearHelper.ClearPreservingCorsHeaders(context.Response);

        Assert.Equal("https://client.example", context.Response.Headers.AccessControlAllowOrigin.ToString());
        Assert.Equal("X-Request-Id", context.Response.Headers.AccessControlExposeHeaders.ToString());
        Assert.Equal("Origin, Accept-Encoding", context.Response.Headers.Vary.ToString());
        Assert.False(context.Response.Headers.ContainsKey("X-Custom-Buffered"));
        Assert.False(context.Response.Headers.ContainsKey(HeaderNames.ETag));
    }
}
