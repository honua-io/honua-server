// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Stac;

[Protocol(TestProtocols.Stac)]
[Collection("Database")]
public sealed class RetiredHostedSampleTests(WebAppFixture fixture) : IClassFixture<WebAppFixture>
{
    [IntegrationTest]
    [Operation(Operations.Infrastructure)]
    public async Task GetHostedSamplePaths_ReturnsNotFound()
    {
        using var client = fixture.CreateClient(allowAutoRedirect: false);
        foreach (var path in new[]
        {
            "/samples/stac-ops", "/samples/stac-ops/", "/samples/stac-ops/index.html",
            "/samples/stac-ops/_framework/blazor.webassembly.js"
        })
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, "the standalone sample is hosted separately");
        }
    }
}
