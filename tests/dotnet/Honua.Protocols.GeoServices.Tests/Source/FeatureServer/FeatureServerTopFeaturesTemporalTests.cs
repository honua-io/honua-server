// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerTopFeaturesTemporalTests
{
    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_TemporalAttributes_ReturnCalendarDateAndEpochTimestamp()
    {
        var access = new AccessPolicy { AllowAnonymous = true };
        var graph = new TestMetadataV2GraphBuilder()
            .AddService("svc", "dates", route: "/rest/services/dates/FeatureServer",
                protocols: [ServiceProtocols.FeatureServer], accessPolicy: access)
            .AddResource("res", "dates", MetadataV2ResourceType.FeatureDataset,
                fields: [
                    new() { Name = "objectid", Type = MetadataV2FieldType.Integer, SemanticRoles = ["id.primary"] },
                    new() { Name = "day", Type = MetadataV2FieldType.Date },
                    new() { Name = "instant", Type = MetadataV2FieldType.DateTime }
                ], accessPolicy: access)
            .AddStorageBinding("binding", "res", "public.dates", storageLayerId: 0)
            .AddPublication("pub", "svc", "res", layerIndex: 0, storageBindingId: "binding",
                serviceLocalId: "0", publicationType: MetadataV2PublicationType.EsriFeatureLayer)
            .BuildProvider();
        var reader = Substitute.For<IFeatureReader>();
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("objectid", 1).Add("day", new DateOnly(2024, 1, 2))
            .Add("instant", "2024-01-02T01:00:00+01:00"));
        reader.QueryTopFeaturesAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, [feature]));
        using var factory = new TestWebApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IMetadataV2GraphProvider>(graph);
                services.AddSingleton<IMetadataV2GraphStore>(graph);
                services.AddSingleton(reader);
            }));
        using var client = factory.CreateClient();
        var filter = Uri.EscapeDataString("{\"groupByFields\":\"day\",\"topCount\":1,\"orderByFields\":\"objectid DESC\"}");
        var response = await client.GetAsync($"/rest/services/dates/FeatureServer/0/queryTopFeatures?topFilter={filter}&f=json");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var attributes = document.RootElement.GetProperty("features")[0].GetProperty("attributes");
        // Calendar dates stay ISO calendar days (esriFieldTypeDateOnly); timestamps stay epoch milliseconds.
        attributes.GetProperty("day").GetString().Should().Be("2024-01-02");
        attributes.GetProperty("instant").GetInt64().Should().Be(1704153600000L);
    }
}
