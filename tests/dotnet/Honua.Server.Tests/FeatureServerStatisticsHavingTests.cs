// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for the FeatureServer statistics <c>having</c> predicate
/// (GeoServices REST conformance, honua-server#1772). Verifies that a HAVING
/// clause actually filters aggregated groups rather than being parsed and ignored.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Collection("Database.CoreSpatial")]
public sealed class FeatureServerStatisticsHavingTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        // Seed two groups: "having_alpha" has 6 rows (passes COUNT > 5),
        // "having_beta" has 2 rows (fails COUNT > 5).
        for (var i = 0; i < 6; i++)
        {
            await _fixture.InsertFeatureAsync(TestLayerId, "having_alpha");
        }

        for (var i = 0; i < 2; i++)
        {
            await _fixture.InsertFeatureAsync(TestLayerId, "having_beta");
        }
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryStatistics_WithHavingPredicate_FiltersAggregatedGroups()
    {
        var outStatistics = Uri.EscapeDataString(
            "[{\"statisticType\":\"count\",\"onStatisticField\":\"objectid\",\"outStatisticFieldName\":\"cnt\"}]");
        var having = Uri.EscapeDataString("COUNT(objectid) > 5");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            "?where=name+LIKE+'having_%'" +
            "&groupByFieldsForStatistics=name" +
            $"&outStatistics={outStatistics}" +
            $"&having={having}" +
            "&f=json");

        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var features = document.RootElement.GetProperty("features");

        var groups = features.EnumerateArray()
            .Select(f => f.GetProperty("attributes").GetProperty("name").GetString())
            .ToArray();

        // The HAVING predicate must keep only the group whose count exceeds 5.
        groups.Should().ContainSingle()
            .Which.Should().Be("having_alpha");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryStatistics_WithoutHaving_ReturnsAllGroups()
    {
        var outStatistics = Uri.EscapeDataString(
            "[{\"statisticType\":\"count\",\"onStatisticField\":\"objectid\",\"outStatisticFieldName\":\"cnt\"}]");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            "?where=name+LIKE+'having_%'" +
            "&groupByFieldsForStatistics=name" +
            $"&outStatistics={outStatistics}" +
            "&f=json");

        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var features = document.RootElement.GetProperty("features");

        var groups = features.EnumerateArray()
            .Select(f => f.GetProperty("attributes").GetProperty("name").GetString())
            .ToArray();

        // Control: without HAVING both seeded groups come back.
        groups.Should().Contain("having_alpha").And.Contain("having_beta");
    }
}
