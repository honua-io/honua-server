// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Database-backed application whose layer 0 marks <c>category</c> (string) and
/// <c>timestamp</c> (date) as publisher-hidden.
/// </summary>
public sealed class FeatureServerAnalyticsHiddenFieldFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .ReplaceService<IH3CapabilityChecker>(new AlwaysAvailableH3Checker());

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
        App.UpdateV2ResourceSchemaField(0, new MetadataV2Field
        {
            Name = "timestamp",
            Type = MetadataV2FieldType.DateTime,
            Nullable = true,
            Hidden = true
        });
    }

    public Task DisposeAsync() => App.DisposeAsync();

    private sealed class AlwaysAvailableH3Checker : IH3CapabilityChecker
    {
        public Task<bool?> IsH3AvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<bool?>(true);
    }
}

/// <summary>
/// FeatureServer analytics endpoints refuse publisher-hidden fields exactly like
/// <c>/query</c> outStatistics, so distinct values, class breaks and bin counts of a
/// hidden column cannot be read (#6098).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerAnalyticsHiddenFieldTests(FeatureServerAnalyticsHiddenFieldFixture fixture)
    : IClassFixture<FeatureServerAnalyticsHiddenFieldFixture>
{
    private const string LayerPath = "/rest/services/test/FeatureServer/0";
    private readonly WebAppFixture _fixture = fixture.App;

    [IntegrationTheory]
    [InlineData("""{"type":"uniqueValueDef","uniqueValueFields":["category"]}""")]
    [InlineData("""{"type":"uniqueValueDef","uniqueValueFields":["name","CATEGORY"]}""")]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/generateRenderer")]
    public async Task GenerateRenderer_HiddenUniqueValueField_Returns400(string classificationDef)
    {
        using var response = await _fixture.Client.GetAsync(
            $"{LayerPath}/generateRenderer?classificationDef={Uri.EscapeDataString(classificationDef)}&f=json");
        var body = await response.Content.ReadAsStringAsync();
        await response.AssertGeoServicesErrorAsync((int)HttpStatusCode.BadRequest);
        body.Should().NotContain("uniqueValueInfos").And.NotContain("sample");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/generateRenderer")]
    public async Task GenerateRenderer_VisibleUniqueValueField_StillClassifies()
    {
        const string classificationDef = """{"type":"uniqueValueDef","uniqueValueFields":["name"]}""";
        using var response = await _fixture.Client.GetAsync(
            $"{LayerPath}/generateRenderer?classificationDef={Uri.EscapeDataString(classificationDef)}&f=json");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("uniqueValueInfos").And.Contain("Test Feature");
    }

    [IntegrationTheory]
    [InlineData("""{"classificationBin":{"field":"category"}}""", null)]
    [InlineData("""{"classificationBin":{"field":"name"}}""",
        """[{"statisticType":"count","onStatisticField":"category","outStatisticFieldName":"n"}]""")]
    [Operation(Operations.QueryBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryBins")]
    public async Task QueryBins_HiddenBinOrStatisticField_Returns400(string bin, string? outStatistics)
    {
        var url = $"{LayerPath}/queryBins?bin={Uri.EscapeDataString(bin)}&f=json";
        if (outStatistics is not null)
        {
            url += $"&outStatistics={Uri.EscapeDataString(outStatistics)}";
        }

        using var response = await _fixture.Client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        await response.AssertGeoServicesErrorAsync((int)HttpStatusCode.BadRequest);
        body.Should().NotContain("\"features\"");
    }

    [IntegrationTest]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBins_HiddenBinField_Returns400()
    {
        const string bin = """{"calendarBin":{"unit":"month"}}""";
        using var response = await _fixture.Client.GetAsync(
            $"{LayerPath}/queryDateBins?binField=timestamp&bin={Uri.EscapeDataString(bin)}&f=json");
        var body = await response.Content.ReadAsStringAsync();
        await response.AssertGeoServicesErrorAsync((int)HttpStatusCode.BadRequest);
        body.Should().NotContain("\"features\"");
    }

    [IntegrationTheory]
    [InlineData("""[{"id":"byCategory","kind":"category","field":"category","limit":5}]""", null)]
    [InlineData("""[{"id":"featureCount","kind":"count"}]""", """[{"field":"category"}]""")]
    [Operation(Operations.QueryH3)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_HiddenSummaryOrGroupByField_Returns400(string summaries, string? groupBy)
    {
        var groupByMember = groupBy is null ? string.Empty : $",\"groupBy\":{groupBy}";
        var payload = $$"""{"resolution":{"indexResolution":7,"model":"h3"},"summaries":{{summaries}}{{groupByMember}},"f":"json"}""";
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _fixture.Client.PostAsync($"{LayerPath}/queryH3", content);
        var body = await response.Content.ReadAsStringAsync();
        await response.AssertGeoServicesErrorAsync((int)HttpStatusCode.BadRequest);
        body.Should().NotContain("sample");
    }
}
