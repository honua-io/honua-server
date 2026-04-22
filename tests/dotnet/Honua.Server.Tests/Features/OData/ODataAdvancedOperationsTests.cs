// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OData;

/// <summary>
/// Integration tests for advanced OData endpoints required by API surface coverage.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public class ODataAdvancedOperationsTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public ODataAdvancedOperationsTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithEmptyRequests_ReturnsClientErrorOrUnauthorized()
    {
        var content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/odata/$batch", content);

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.Unauthorized,
            $"Expected BadRequest or Unauthorized, got {response.StatusCode}.");
    }

    [IntegrationTest]
    [Operation(Operations.ODataApply)]
    [Endpoint("GET /odata/Features({layerId})/$apply")]
    public async Task Apply_WithoutApplyParameter_ReturnsClientErrorOrUnauthorized()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({WebAppFixture.TestLayerId})/$apply");

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.Unauthorized,
            $"Expected BadRequest or Unauthorized, got {response.StatusCode}.");
    }

    [IntegrationTest]
    [Operation(Operations.ODataSearch)]
    [Endpoint("GET /odata/Features({layerId})/$search")]
    public async Task Search_WithoutSearchParameter_ReturnsClientErrorOrUnauthorized()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({WebAppFixture.TestLayerId})/$search");

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.Unauthorized,
            $"Expected BadRequest or Unauthorized, got {response.StatusCode}.");
    }
}
