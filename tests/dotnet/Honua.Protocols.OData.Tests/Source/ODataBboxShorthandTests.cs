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
/// Verifies the OData <c>bbox</c> spatial shorthand maps to a canonical envelope spatial
/// filter so viewport windowing does not require a verbose geo.intersects WKT polygon
/// (issue #1621). Cities reference odata.yaml seed coordinates.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataBboxShorthandTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder => builder.UseSetting("OData:MaxPageSize", "2").UseSetting("OData:MaxApplyInputRows", "2"));
    private const int TestLayerId = 0;
    private const long SanFranciscoId = 1;   // -122.4194, 37.7749
    private const long LosAngelesId = 2;     // -118.2437, 34.0522
    private const long SacramentoId = 3;     // -121.4944, 38.5816
    private const long SeattleId = 6;        // -122.3321, 47.6062

    public async Task InitializeAsync()
    {
        // All segments are relative literal path fragments (not user input), so none can be
        // rooted and silently drop earlier arguments.
        _fixture.UseSeed(Path.Join("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers({layerId})/Features")]
    public async Task PowerQuery_DocumentedFeed_LoadsFeatureRows()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Join(directory.FullName, "docs", "guides", "connect", "excel-power-bi.md")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();
        var guide = await File.ReadAllTextAsync(Path.Join(directory!.FullName, "docs", "guides", "connect", "excel-power-bi.md"));
        var match = System.Text.RegularExpressions.Regex.Match(guide, "OData\\.Feed\\(\\s*\"(?<url>[^\"]+)\"");
        match.Success.Should().BeTrue();
        var source = match.Groups["url"].Value.Replace("{layerId}", "0", StringComparison.Ordinal);
        var response = await _fixture.Client.GetAsync(new Uri(source).PathAndQuery);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await ParseFeaturesAsync(response);
        rows.Should().NotBeEmpty();
        rows.Select(row => row.TryGetProperty("ObjectId", out _)).Should().OnlyContain(found => found,
            "the documented Power Query source must load feature rows");
    }

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Layers({layerId})/Features")]
    public async Task Bbox_NextLink_StaysInsideWindow()
    {
        const string bbox = "-124,32,-114,42";
        var response = await _fixture.Client.GetAsync($"/odata/Layers(0)/Features?bbox={bbox}&$orderby=ObjectId&$select=ObjectId");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var link = page.RootElement.GetProperty("@odata.nextLink").GetString()!;
        var parameters = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(link).Query);
        parameters["bbox"].ToString().Should().Be(bbox);

        var next = await _fixture.Client.GetAsync(new Uri(link).PathAndQuery);
        next.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await ParseFeaturesAsync(next);
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(row => row.GetProperty("ObjectId").GetInt64() <= 5);
    }

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?bbox=minLon,minLat,maxLon,maxLat")]
    public async Task Bbox_WindowAroundBayArea_ReturnsFeaturesInsideEnvelope()
    {
        // Envelope around the San Francisco Bay Area / Sacramento.
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?bbox=-123,37,-121,39");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        objectIds.Should().Contain(SanFranciscoId);
        objectIds.Should().Contain(SacramentoId);
        objectIds.Should().NotContain(LosAngelesId);
        objectIds.Should().NotContain(SeattleId);
    }

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?bbox=...&$filter=...")]
    public async Task Bbox_CombinedWithAttributeFilter_ReturnsIntersection()
    {
        // California envelope AND only capitals -> Sacramento only.
        var filter = "is_capital eq true";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?bbox=-124,32,-114,42&$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        features.Should().NotBeEmpty();
        foreach (var feature in features)
        {
            feature.GetProperty("is_capital").GetBoolean().Should().BeTrue();
        }

        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        objectIds.Should().Contain(SacramentoId);
        objectIds.Should().NotContain(SanFranciscoId);
    }

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?bbox=<invalid>")]
    public async Task Bbox_InvalidValue_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?bbox=not-a-bbox");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers({layerId})/Features")]
    public async Task Apply_ConfiguredInputBudget_RejectsOverflowAndAcceptsExactLimit()
    {
        var overflow = await _fixture.Client.GetAsync("/odata/Layers(0)/Features?$apply=aggregate($count%20as%20n)");
        overflow.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await overflow.Content.ReadAsStringAsync()).Should().Contain("maximum input row count of 2");

        var exact = await _fixture.Client.GetAsync(
            "/odata/Layers(0)/Features?$apply=aggregate($count%20as%20n)&$filter=ObjectId%20lt%203");
        exact.StatusCode.Should().Be(HttpStatusCode.OK, await exact.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await exact.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("value")[0].GetProperty("n").GetInt64().Should().Be(2);
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
