// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Grouped statistics are capped by the transfer limit, not the smaller feature page default.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerStatisticsTransferLimitTests(FeatureServerStatisticsTransferLimitFixture fixture)
    : IClassFixture<FeatureServerStatisticsTransferLimitFixture>
{
    private readonly WebAppFixture _fixture = fixture.App;

    [IntegrationTheory]
    [InlineData(false, null)]
    [InlineData(false, "500")]
    [InlineData(true, null)]
    [InlineData(true, "500")]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/query")]
    public async Task Statistics_UnpagedOrOversizedPage_ReturnsMaxRecordCountGroups(bool serviceQuery, string? resultRecordCount)
    {
        var parameters = new Dictionary<string, string>
        {
            ["f"] = "json",
            ["where"] = "1=1",
            ["outStatistics"] = """[{"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":"n"}]""",
            ["groupByFieldsForStatistics"] = "objectid",
            ["orderByFields"] = "objectid ASC",
            ["returnGeometry"] = "false"
        };
        if (resultRecordCount != null)
        {
            parameters["resultRecordCount"] = resultRecordCount;
        }
        if (serviceQuery)
        {
            parameters["layers"] = "0";
        }

        using var form = new FormUrlEncodedContent(parameters);
        using var response = await _fixture.Client.PostAsync(
            "/rest/services/test/FeatureServer/" + (serviceQuery ? "query" : "0/query"), form);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var payload = serviceQuery ? document.RootElement.GetProperty("layers")[0] : document.RootElement;
        payload.GetProperty("features").GetArrayLength().Should().Be(FeatureServerStatisticsTransferLimitFixture.MaxRecordCount);
        payload.GetProperty("exceededTransferLimit").GetBoolean().Should().BeTrue();
    }
}

public sealed class FeatureServerStatisticsTransferLimitFixture : IAsyncLifetime
{
    public const int MaxRecordCount = 101;

    public WebAppFixture App { get; } = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, configBuilder) =>
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Limits:Query:DefaultRecordCount"] = "100",
                ["Limits:Query:MaxRecordCount"] = MaxRecordCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            })));

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        await App.EnsureLargeTestDatasetAsync();
    }

    public Task DisposeAsync() => App.DisposeAsync();
}
