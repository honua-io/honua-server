// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.OData;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.OData;

[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataExpansionBudgetRegressionTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Join("tests", "seed", "odata.yaml"));
        _fixture.ConfigureServices(services =>
            services.Configure<ODataOptions>(options => options.MaxPageSize = 1));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ODataExpand)]
    [Endpoint("GET /odata/Layers({layerId})/Features")]
    public async Task Expand_OneRowPage_DoesNotMaterializeUnpagedChildren()
    {
        // The ordinary seed already has two landmarks for city 1; no scale fixture is needed.
        using var response = await _fixture.Client.GetAsync(
            "/odata/Layers(0)/Features?$filter=ObjectId eq 1&$top=1&$expand=Landmarks");

        // Explicitly refusing an expansion that exceeds the budget is also bounded behavior.
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge)
        {
            return;
        }

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var parents = body.RootElement.GetProperty("value");
        parents.GetArrayLength().Should().Be(1);
        var parent = parents[0];
        var children = parent.GetProperty("Landmarks");
        children.GetArrayLength().Should().BeLessThanOrEqualTo(1,
            "the configured OData page budget must also bound expanded relationship rows");
        parent.TryGetProperty("Landmarks@odata.nextLink", out _).Should().BeTrue(
            "the second seeded landmark must remain reachable rather than being silently truncated");
    }
}
