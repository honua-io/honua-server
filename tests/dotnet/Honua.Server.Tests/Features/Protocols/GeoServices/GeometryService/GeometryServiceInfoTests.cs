// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

/// <summary>
/// Covers the GeometryServer root-info endpoint that Esri REST clients probe
/// before invoking operations. Without this endpoint the client's discovery
/// handshake fails and no operation request is ever made.
/// </summary>
[Protocol(TestProtocols.GeometryService)]
[Collection("Database")]
public sealed class GeometryServiceInfoTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer")]
    public async Task GeometryServerInfo_ReturnsServiceDescriptor()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/Utilities/Geometry/GeometryServer");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        root.TryGetProperty("currentVersion", out var currentVersion).Should().BeTrue();
        currentVersion.GetDouble().Should().BeGreaterThan(0);
        root.TryGetProperty("serviceDescription", out _).Should().BeTrue();
        root.TryGetProperty("maxBufferCount", out var maxBuffer).Should().BeTrue();
        maxBuffer.GetInt32().Should().BeGreaterThan(0);
        root.TryGetProperty("maxSimplifyCount", out _).Should().BeTrue();
    }
}
