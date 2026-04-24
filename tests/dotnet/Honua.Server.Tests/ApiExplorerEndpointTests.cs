// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for the interactive API explorer (Scalar) at /docs.
/// </summary>
[Protocol(TestProtocols.Infrastructure)]
[Collection("Database")]
public sealed class ApiExplorerEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder => builder.UseSetting("HONUA_SERVE_API_DOCS", "true"));

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /docs")]
    public async Task Docs_WhenEnabled_ReturnsHtmlPage()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/docs");

        // Assert
        response.Be200Ok();
        var contentType = response.Content.Headers.ContentType?.MediaType;
        contentType.Should().Be("text/html");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Honua API Explorer");
    }
}
