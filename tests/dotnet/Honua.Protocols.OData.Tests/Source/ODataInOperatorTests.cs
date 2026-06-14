// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// Verifies the OData $filter <c>in</c> operator maps to the shared canonical filter
/// pipeline so multi-select category filters do not require manual or-chains (issue #1621).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataInOperatorTests : IAsyncLifetime
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
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$filter=<field> in (...)")]
    public async Task Filter_InOperatorWithStringList_ReturnsMatchingFeatures()
    {
        var filter = "state in ('Washington','Oregon')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        features.Should().NotBeEmpty();
        var states = features
            .Select(f => f.GetProperty("state").GetString())
            .ToArray();
        states.Should().OnlyContain(s => s == "Washington" || s == "Oregon");
        states.Should().Contain("Washington");
        states.Should().Contain("Oregon");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$filter=ObjectId in (...)")]
    public async Task Filter_InOperatorWithNumericList_ReturnsMatchingFeatures()
    {
        var filter = "objectid in (1,2,3)";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        objectIds.Should().BeEquivalentTo(new long[] { 1, 2, 3 });
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$filter=<field> in (...) and ...")]
    public async Task Filter_InOperatorCombinedWithAttributeFilter_ReturnsCombinedResult()
    {
        var filter = "state in ('California','Arizona') and is_capital eq true";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        features.Should().NotBeEmpty();
        foreach (var feature in features)
        {
            feature.GetProperty("is_capital").GetBoolean().Should().BeTrue();
            var state = feature.GetProperty("state").GetString();
            state.Should().BeOneOf("California", "Arizona");
        }
    }

    private static async Task<List<JsonElement>> ParseFeaturesAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        return document.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(e => e.Clone())
            .ToList();
    }
}
