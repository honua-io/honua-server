// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using System.Text.Json;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for FeatureServer query input-validation edge cases
/// (honua-server#1823). A class of malformed query inputs used to bypass up-front
/// validation and crash at SQL-execution time as a 500; each must instead be
/// rejected as a structured 400:
/// <list type="bullet">
/// <item>arithmetic errors in <c>where</c> (division/modulo by zero, overflow,
/// invalid LOG/POWER/SQRT argument, bad CAST) raised at eval time;</item>
/// <item>a numeric aggregate (avg/sum/stddev/var) applied to a string field;</item>
/// <item>a <c>groupByFieldsForStatistics</c> referencing an unknown column.</item>
/// </list>
/// The seeded <c>test</c> layer (id 0) exposes a numeric <c>objectid</c> and a
/// string <c>name</c> field.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Collection("Database.CoreSpatial")]
public sealed class FeatureServerQueryValidationEdgeCaseTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await _fixture.InsertFeatureAsync(TestLayerId, "edge_one");
        await _fixture.InsertFeatureAsync(TestLayerId, "edge_two");
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Theory]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [InlineData("objectid = 1/0")]
    [InlineData("objectid = 10 % 0")]
    [InlineData("objectid = POWER(2, 5000)")]
    [InlineData("objectid = SQRT(-1)")]
    [InlineData("objectid = LOG(-1)")]
    [InlineData("objectid = CAST('abc' AS INTEGER)")]
    public async Task Query_WhereArithmeticError_Returns400NotServerError(string where)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?where={Uri.EscapeDataString(where)}" +
            "&f=json");

        response.Be400BadRequest();
    }

    [Theory]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [InlineData("avg")]
    [InlineData("sum")]
    [InlineData("stddev")]
    [InlineData("var")]
    public async Task Query_NumericAggregateOnStringField_Returns400(string statisticType)
    {
        var outStatistics = Uri.EscapeDataString(
            $"[{{\"statisticType\":\"{statisticType}\",\"onStatisticField\":\"name\",\"outStatisticFieldName\":\"agg\"}}]");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?outStatistics={outStatistics}" +
            "&f=json");

        response.Be400BadRequest();
    }

    [Theory]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [InlineData("min")]
    [InlineData("max")]
    [InlineData("count")]
    public async Task Query_ComparableAggregateOnStringField_Succeeds(string statisticType)
    {
        // Control: min/max/count are valid on any comparable type, including a
        // string field, and must continue to return 200.
        var outStatistics = Uri.EscapeDataString(
            $"[{{\"statisticType\":\"{statisticType}\",\"onStatisticField\":\"name\",\"outStatisticFieldName\":\"agg\"}}]");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?outStatistics={outStatistics}" +
            "&f=json");

        response.Be200Ok();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_GroupByUnknownColumn_Returns400()
    {
        var outStatistics = Uri.EscapeDataString(
            "[{\"statisticType\":\"count\",\"onStatisticField\":\"objectid\",\"outStatisticFieldName\":\"cnt\"}]");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            "?groupByFieldsForStatistics=does_not_exist" +
            $"&outStatistics={outStatistics}" +
            "&f=json");

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_GroupByKnownColumn_Succeeds()
    {
        // Control: a groupBy on a real column still returns 200.
        var outStatistics = Uri.EscapeDataString(
            "[{\"statisticType\":\"count\",\"onStatisticField\":\"objectid\",\"outStatisticFieldName\":\"cnt\"}]");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            "?groupByFieldsForStatistics=name" +
            $"&outStatistics={outStatistics}" +
            "&f=json");

        response.Be200Ok();
    }

    // BH7-003 regression: outStatistics with groupByFieldsForStatistics must apply
    // the configured MaxRecordCount cap (not null) so a high-cardinality groupBy
    // cannot exhaust server memory. With few test rows the cap is never hit; the
    // test guards the code path and verifies no regression in the normal case.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_OutStatisticsGroupByWithCap_ReturnsGroupedResultsWithinLimit()
    {
        var outStatistics = Uri.EscapeDataString(
            "[{\"statisticType\":\"count\",\"onStatisticField\":\"objectid\",\"outStatisticFieldName\":\"feature_count\"}]");

        // Each of the two seeded features has a distinct name, so the groupBy produces
        // one statistics row per name — well within the MaxRecordCount cap.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            "?where=1%3D1" +
            "&groupByFieldsForStatistics=name" +
            $"&outStatistics={outStatistics}" +
            "&f=json");

        // Must succeed — the cap must not break valid queries below the threshold.
        response.Be200Ok();

        var body = await response.Content.ReadAsStringAsync();
        var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("features", out var features).Should().BeTrue();
        // Two distinct names -> two groups; both within the default MaxRecordCount cap.
        features.GetArrayLength().Should().Be(2);
    }
}
