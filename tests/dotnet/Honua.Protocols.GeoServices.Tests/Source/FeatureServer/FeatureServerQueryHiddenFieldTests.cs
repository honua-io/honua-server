// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed class FeatureServerQueryHiddenFieldFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new();

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        App.UpdateV2ResourceSchemaField(0, new MetadataV2Field
        {
            Name = "category",
            Type = MetadataV2FieldType.String,
            Nullable = true,
            Hidden = true
        });
    }

    public Task DisposeAsync() => App.DisposeAsync();
}

// A hidden field is left out of query responses, so a query may not filter or order on it either.
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerQueryHiddenFieldTests(FeatureServerQueryHiddenFieldFixture fixture)
    : IClassFixture<FeatureServerQueryHiddenFieldFixture>
{
    private const string LayerPath = "/rest/services/test/FeatureServer/0";
    private const string QueryPath = LayerPath + "/query";
    private readonly WebAppFixture _fixture = fixture.App;

    [IntegrationTheory]
    [Operation(Operations.Query)]
    [InlineData("category = 'a'")]
    [InlineData("name = 'x' OR CATEGORY IS NOT NULL")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WhereNamesHiddenField_Returns400(string where)
    {
        using var response = await _fixture.Client.GetAsync(
            $"{QueryPath}?where={Uri.EscapeDataString(where)}&outFields=name&f=json");
        var body = await response.Content.ReadAsStringAsync();
        await response.AssertGeoServicesErrorAsync((int)HttpStatusCode.BadRequest);
        body.Should().NotContain("\"features\"").And.NotContain("hidden");
    }

    [IntegrationTheory]
    [Operation(Operations.Query)]
    [InlineData("category")]
    [InlineData("name ASC, category DESC")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_OrderByHiddenField_Returns400(string orderByFields)
    {
        using var response = await _fixture.Client.GetAsync(
            $"{QueryPath}?where=1%3D1&orderByFields={Uri.EscapeDataString(orderByFields)}&outFields=name&f=json");
        var body = await response.Content.ReadAsStringAsync();
        await response.AssertGeoServicesErrorAsync((int)HttpStatusCode.BadRequest);
        body.Should().NotContain("\"features\"");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WhereNamesHiddenField_AnswersLikeAnUndeclaredField()
    {
        var hidden = await GetBodyAsync($"{QueryPath}?where={Uri.EscapeDataString("category = 'a'")}&f=json");
        var undeclared = await GetBodyAsync($"{QueryPath}?where={Uri.EscapeDataString("nosuchf = 'a'")}&f=json");

        WithoutRequestStamps(hidden.Replace("category", "nosuchf", StringComparison.Ordinal)).Should().Be(WithoutRequestStamps(undeclared));
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WhereAndOrderByOnVisibleField_StillAnswer()
    {
        using var response = await _fixture.Client.GetAsync(
            $"{QueryPath}?where={Uri.EscapeDataString("name IS NOT NULL")}&orderByFields=name&outFields=name&f=json");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("\"features\"").And.NotContain("\"error\"");
    }

    [IntegrationTheory]
    [Operation(Operations.ValidateSql)]
    [InlineData("where", "category = 'a'")]
    [InlineData("orderBy", "category DESC")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL")]
    public async Task ValidateSql_HiddenField_IsInvalidLikeAnUndeclaredField(string sqlType, string sql)
    {
        var hidden = await GetBodyAsync($"{LayerPath}/validateSQL?sqlType={sqlType}&sql={Uri.EscapeDataString(sql)}&f=json");
        var undeclared = await GetBodyAsync(
            $"{LayerPath}/validateSQL?sqlType={sqlType}&sql={Uri.EscapeDataString(sql.Replace("category", "nosuchf", StringComparison.Ordinal))}&f=json");

        using var document = JsonDocument.Parse(hidden);
        document.RootElement.GetProperty("isValidSQL").GetBoolean().Should().BeFalse();
        hidden.Replace("category", "nosuchf", StringComparison.Ordinal).Should().Be(undeclared);
    }

    [IntegrationTheory]
    [Operation(Operations.QueryTopFeatures)]
    [InlineData("name", "category desc")]
    [InlineData("category", "name desc")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_TopFilterNamesHiddenField_Returns400(string groupByFields, string orderByFields)
    {
        var topFilter = JsonSerializer.Serialize(new { groupByFields, topCount = 1, orderByFields });
        using var response = await _fixture.Client.GetAsync(
            $"{LayerPath}/queryTopFeatures?topFilter={Uri.EscapeDataString(topFilter)}&f=json");
        var body = await response.Content.ReadAsStringAsync();
        await response.AssertGeoServicesErrorAsync((int)HttpStatusCode.BadRequest);
        body.Should().NotContain("\"features\"");
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_WhereNamesHiddenField_Returns400()
    {
        var topFilter = JsonSerializer.Serialize(new { groupByFields = "name", topCount = 1, orderByFields = "objectid desc" });
        using var response = await _fixture.Client.GetAsync(
            $"{LayerPath}/queryTopFeatures?topFilter={Uri.EscapeDataString(topFilter)}&where={Uri.EscapeDataString("category = 'a'")}&f=json");
        await response.AssertGeoServicesErrorAsync((int)HttpStatusCode.BadRequest);
    }

    // Error bodies carry the request's W3C trace id and a timestamp, which differ between any two requests.
    private static string WithoutRequestStamps(string body)
        => Regex.Replace(
            Regex.Replace(body, "00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}", "<trace>"),
            @"\d{4}-\d{2}-\d{2}T[0-9:.]+(Z|\\u002B\d{2}:\d{2}|[+-]\d{2}:\d{2})?",
            "<time>");

    private async Task<string> GetBodyAsync(string url)
    {
        using var response = await _fixture.Client.GetAsync(url);
        return await response.Content.ReadAsStringAsync();
    }
}
