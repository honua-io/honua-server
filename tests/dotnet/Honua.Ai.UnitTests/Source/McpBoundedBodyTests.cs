// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed class McpBoundedBodyTests
{
    [UnitTest]
    public void BoundedBodyInitialCapacity_IgnoresDeclaredTenMegabyteLength()
    {
        var declared = new McpOptions().MaxRequestBodyBytes;

        McpEndpointExtensions.BoundedBodyInitialCapacity(declared).Should().Be(16 * 1024);
    }

    [UnitTest]
    public async Task ReadBoundedBody_SmallBodyWithLargeContentLength_ReadsBytes()
    {
        var maxBytes = new McpOptions().MaxRequestBodyBytes;
        var payload = Encoding.UTF8.GetBytes("{\"ok\":true}");
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(payload);
        context.Request.ContentLength = payload.Length;
        context.Request.ContentLength.Should().BeLessThan(maxBytes);

        var body = await McpEndpointExtensions.ReadBoundedBodyAsync(context.Request, maxBytes, CancellationToken.None);

        body.Should().NotBeNull();
        Encoding.UTF8.GetString(body!.Value.Span).Should().Be("{\"ok\":true}");
        McpEndpointExtensions.BoundedBodyInitialCapacity(maxBytes).Should().Be(16 * 1024);
    }
}
