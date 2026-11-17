// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.OData;

[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataLayerFilterTests(ODataProHost host) : IClassFixture<ODataProHost>
{
    private const string CollectionOptions = "&$orderby=ObjectId&$select=ObjectId,LayerId&$count=true&$top=100";

    [IntegrationTheory]
    [InlineData("name ne 'Boise'")]
    [InlineData("notes ne 'City of Trees'")]
    [InlineData("name eq 'Boise' or name eq 'Seattle'")]
    [InlineData("not (name eq 'Boise')")]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features")]
    public async Task Features_SingleLayerWithAttributeBooleanFilter_MatchesScopedQuery(string predicate)
    {
        var (expected, actual) = await GetScopedAndGlobalAsync(predicate, "", CollectionOptions);

        using var expectedJson = JsonDocument.Parse(expected);
        using var actualJson = JsonDocument.Parse(actual);
        var rows = actualJson.RootElement.GetProperty("value");
        rows.GetRawText().Should().Be(expectedJson.RootElement.GetProperty("value").GetRawText());
        rows.GetArrayLength().Should().BePositive();
        rows.EnumerateArray().Should().OnlyContain(row => row.GetProperty("LayerId").GetInt32() == 0);
        actualJson.RootElement.GetProperty("@odata.count").GetInt64().Should()
            .Be(expectedJson.RootElement.GetProperty("@odata.count").GetInt64());
    }

    [IntegrationTheory]
    [InlineData("name ne 'Boise'")]
    [InlineData("notes ne 'City of Trees'")]
    [InlineData("name eq 'Boise' or name eq 'Seattle'")]
    [InlineData("not (name eq 'Boise')")]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features/$count")]
    public async Task FeaturesCount_SingleLayerWithAttributeBooleanFilter_MatchesScopedCount(string predicate)
    {
        var (expected, actual) = await GetScopedAndGlobalAsync(predicate, "/$count", "");

        actual.Should().Be(expected);
        long.Parse(actual, System.Globalization.CultureInfo.InvariantCulture).Should().BePositive();
    }

    [IntegrationTheory]
    [MemberData(nameof(FiltersWithoutSingleLayerGuarantee))]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features")]
    public async Task Features_FilterWithoutSingleLayerGuarantee_ReturnsBadRequest(string filter)
    {
        await AssertBadRequestAsync("/odata/Features", filter);
    }

    [IntegrationTheory]
    [MemberData(nameof(FiltersWithoutSingleLayerGuarantee))]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features/$count")]
    public async Task FeaturesCount_FilterWithoutSingleLayerGuarantee_ReturnsBadRequest(string filter)
    {
        await AssertBadRequestAsync("/odata/Features/$count", filter);
    }

    public static TheoryData<string> FiltersWithoutSingleLayerGuarantee() => new()
    {
        "LayerId eq 0 or LayerId eq 1",
        "LayerId eq 0 or name eq 'Boise'",
        "name ne 'Boise'",
        "not (LayerId eq 0)",
    };

    private async Task<(string Expected, string Actual)> GetScopedAndGlobalAsync(
        string predicate, string suffix, string options)
    {
        using var scoped = await host.Fixture.Client.GetAsync(
            $"/odata/Layers(0)/Features{suffix}?$filter={Uri.EscapeDataString(predicate)}{options}");
        scoped.StatusCode.Should().Be(HttpStatusCode.OK);
        var expected = await scoped.Content.ReadAsStringAsync();

        var filter = Uri.EscapeDataString($"LayerId eq 0 and ({predicate})");
        using var global = await host.Fixture.Client.GetAsync($"/odata/Features{suffix}?$filter={filter}{options}");
        global.StatusCode.Should().Be(HttpStatusCode.OK);
        return (expected, await global.Content.ReadAsStringAsync());
    }

    private async Task AssertBadRequestAsync(string path, string filter)
    {
        using var response = await host.Fixture.Client.GetAsync($"{path}?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
