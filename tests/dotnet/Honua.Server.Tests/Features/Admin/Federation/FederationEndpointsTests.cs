// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Federation.Domain;
using Honua.Server.Features.Admin.Federation;
using Honua.Server.Features.Admin.Federation.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin.Federation;

/// <summary>
/// Integration tests for the federation admin surface (issue #341). Sources are supplied
/// through the bound <c>Federation</c> configuration section, so these tests exercise the
/// configuration-backed registry and the offline query planner end to end without contacting
/// any remote source.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class FederationEndpointsTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;
    private System.Net.Http.HttpClient _client = null!;

    public FederationEndpointsTests()
    {
        // Bind the federation source list through the same options the production registry
        // reads. ConfigureServices maps onto ConfigureTestServices, which the fixture applies
        // last so this post-configuration wins over the empty default.
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.Configure<FederationSourceOptions>(options =>
                {
                    options.Sources = new[]
                    {
                        new FederationSourceConfig
                        {
                            Id = "esri-parcels",
                            DisplayName = "County Parcels (Esri)",
                            Kind = FederatedSourceKind.EsriRest,
                            Endpoint = "https://gis.example.gov/arcgis/rest/services/Parcels/FeatureServer",
                            RemoteLayer = "0",
                            RequestTimeoutSeconds = 15,
                        },
                        new FederationSourceConfig
                        {
                            Id = "peer-honua",
                            Kind = FederatedSourceKind.HonuaGrpc,
                            Endpoint = "https://peer.example.com",
                            RemoteLayer = "roads",
                        },
                    };
                });
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/federation/sources")]
    public async Task ListSources_ReturnsConfiguredSourcesWithoutCredentials()
    {
        var response = await _client.GetAsync("/api/v1/admin/federation/sources");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var sources = await response.Content.ReadFromJsonAsync<FederationSourceResponse[]>(JsonOptions);

        sources.Should().NotBeNull();
        sources!.Should().HaveCount(2);
        sources.Should().Contain(s => s.Id == "esri-parcels" && s.Kind == "EsriRest" && s.RequestTimeoutSeconds == 15);
        sources.Should().Contain(s => s.Id == "peer-honua" && s.Kind == "HonuaGrpc");

        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("password", "federation source descriptors carry no transport credentials");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/federation/sources/{id}/plan")]
    public async Task PlanQuery_EsriRestWithWhereAndBbox_RefinesNothingAndPushesDown()
    {
        var response = await _client.GetAsync("/api/v1/admin/federation/sources/esri-parcels/plan?where=zoning%3D%27R1%27&bbox=true&joinLocal=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var plan = await response.Content.ReadFromJsonAsync<FederationQueryPlanResponse>(JsonOptions);

        plan.Should().NotBeNull();
        plan!.SourceId.Should().Be("esri-parcels");
        plan.RequiresLocalJoin.Should().BeTrue();
        plan.Decisions.Should().Contain(d => d.Predicate == "AttributeFilter" && d.PushedDown);
        plan.Decisions.Should().Contain(d => d.Predicate == "SpatialFilter" && d.PushedDown);
        plan.RequiresLocalRefinement.Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/federation/sources/{id}/plan")]
    public async Task PlanQuery_UnknownSource_Returns404()
    {
        var response = await _client.GetAsync("/api/v1/admin/federation/sources/does-not-exist/plan?where=1%3D1");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
