// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.OData;

[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataLimitsEnforcementTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top={max+1}")]
    public async Task Features_WithTopExceedingMaxRecordCount_ReturnsBadRequest()
    {
        var limits = _fixture.GetService<IOptions<LimitsOptions>>().Value;
        var excessiveTop = limits.Query.MaxRecordCount + 1;

        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$top={excessiveTop}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response, "Maximum record count");
    }

    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$skip={max+1}")]
    public async Task Features_WithSkipExceedingMaxOffset_ReturnsBadRequest()
    {
        var limits = _fixture.GetService<IOptions<LimitsOptions>>().Value;
        var excessiveSkip = limits.Query.MaxOffset + 1;

        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$skip={excessiveSkip}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response, "Maximum offset");
    }

    private static async Task AssertODataErrorAsync(HttpResponseMessage response, string expectedMessageFragment)
    {
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.TryGetProperty("error", out var error).Should().BeTrue();
        error.TryGetProperty("details", out var details).Should().BeTrue();

        var messages = details.EnumerateArray()
            .Select(detail => detail.GetProperty("message").GetString())
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToArray();

        messages.Should().Contain(message => message!.Contains(expectedMessageFragment));
    }
}
