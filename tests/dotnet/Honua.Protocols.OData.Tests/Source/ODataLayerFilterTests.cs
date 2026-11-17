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
    [IntegrationTheory]
    [InlineData("name ne 'Boise'", false)]
    [InlineData("name ne 'Boise'", true)]
    [InlineData("notes ne 'City of Trees'", false)]
    [InlineData("notes ne 'City of Trees'", true)]
    [InlineData("name eq 'Boise' or name eq 'Seattle'", false)]
    [InlineData("name eq 'Boise' or name eq 'Seattle'", true)]
    [InlineData("not (name eq 'Boise')", false)]
    [InlineData("not (name eq 'Boise')", true)]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features")]
    [Endpoint("GET /odata/Features/$count")]
    public async Task Features_SingleLayerWithAttributeBooleanFilter_MatchesScopedQuery(string predicate, bool count)
    {
        var suffix = count ? "/$count" : "";
        var options = count ? "" : "&$orderby=ObjectId&$select=ObjectId,LayerId&$count=true&$top=100";
        using var scoped = await host.Fixture.Client.GetAsync(
            $"/odata/Layers(0)/Features{suffix}?$filter={Uri.EscapeDataString(predicate)}{options}");
        scoped.StatusCode.Should().Be(HttpStatusCode.OK);
        var expected = await scoped.Content.ReadAsStringAsync();

        var filter = Uri.EscapeDataString($"LayerId eq 0 and ({predicate})");
        using var global = await host.Fixture.Client.GetAsync($"/odata/Features{suffix}?$filter={filter}{options}");

        global.StatusCode.Should().Be(HttpStatusCode.OK);
        var actual = await global.Content.ReadAsStringAsync();
        if (count)
        {
            actual.Should().Be(expected);
            long.Parse(actual, System.Globalization.CultureInfo.InvariantCulture).Should().BePositive();
            return;
        }

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
    [InlineData("LayerId eq 0 or LayerId eq 1", false)]
    [InlineData("LayerId eq 0 or LayerId eq 1", true)]
    [InlineData("LayerId eq 0 or name eq 'Boise'", false)]
    [InlineData("LayerId eq 0 or name eq 'Boise'", true)]
    [InlineData("name ne 'Boise'", false)]
    [InlineData("name ne 'Boise'", true)]
    [InlineData("not (LayerId eq 0)", false)]
    [InlineData("not (LayerId eq 0)", true)]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features")]
    [Endpoint("GET /odata/Features/$count")]
    public async Task Features_FilterWithoutSingleLayerGuarantee_ReturnsBadRequest(string filter, bool count)
    {
        var suffix = count ? "/$count" : "";

        using var response = await host.Fixture.Client.GetAsync(
            $"/odata/Features{suffix}?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
