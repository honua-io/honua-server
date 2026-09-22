// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Records;

[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiRecords)]
public sealed class OgcRecordsServiceLayerIdsTests
{
    private const string ServiceName = "records-unique-visible-layers";
    private const string FeatureServiceId = "svc-records-unique-feature";
    private const string MapServiceId = "svc-records-unique-map";
    private const string EntitledRole = "records-layer-reader";
    private const string Referer = "https://records-layer-identity.example/";
    private static readonly int[] StorageLayerIds = [0, 1, 2];
    private static readonly int[] PublicLayerIds = [0, 2];

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task GetServiceRecord_WithProtocolAliases_ListsUniqueVisibleStorageLayerIds()
    {
        await using var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            });
        await fixture.InitializeAsync();
        var snapshot = fixture.GetCurrentV2GraphSnapshot();
        var provider = fixture.GetService<TestMetadataV2GraphProvider>();
        var sourcePublications = StorageLayerIds.Select(id => snapshot.Graph.Publications.First(publication =>
            publication.PublicationType == MetadataV2PublicationType.EsriFeatureLayer &&
            snapshot.ResolveStorageLayerId(publication) == id && snapshot.IsRoutable(publication))).ToArray();
        var sourceService = snapshot.Index.ServicesById[sourcePublications[0].ServiceId];
        var restrictedResourceId = sourcePublications[1].ResourceId;
        var featurePublications = sourcePublications.Select((publication, index) => ClonePublication(
            publication, FeatureServiceId, MetadataV2PublicationType.EsriFeatureLayer, 100 + index)).ToArray();
        var mapPublications = sourcePublications.Select((publication, index) => ClonePublication(
            publication, MapServiceId, MetadataV2PublicationType.EsriMapLayer, 200 + index)).ToArray();
        var featureService = sourceService with
        {
            Metadata = sourceService.Metadata with { Id = FeatureServiceId, Name = ServiceName },
            ServiceType = MetadataV2ServiceType.EsriFeatureService,
            Protocols = [ServiceProtocols.FeatureServer],
            PublicationIds = featurePublications.Select(publication => publication.Metadata.Id).ToArray(),
            AccessPolicy = new AccessPolicy { AllowAnonymous = true }
        };
        var mapService = featureService with
        {
            Metadata = featureService.Metadata with { Id = MapServiceId },
            ServiceType = MetadataV2ServiceType.EsriMapService,
            Protocols = [ServiceProtocols.MapServer],
            PublicationIds = mapPublications.Select(publication => publication.Metadata.Id).ToArray()
        };
        var graph = snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Services = [.. snapshot.Graph.Services, mapService, featureService],
            Publications = [.. snapshot.Graph.Publications, .. mapPublications, .. featurePublications],
            Resources = snapshot.Graph.Resources.Select(resource => resource.Metadata.Id == restrictedResourceId
                ? resource with { AccessPolicy = new AccessPolicy { AllowAnonymous = false, AllowedRoles = [EntitledRole] } }
                : resource).ToArray()
        };
        try
        {
            provider.SetGraph(graph, schema: fixture.CurrentSchema);
            var entitled = await IssueAsync(fixture, "records-entitled", EntitledRole);
            var unentitled = await IssueAsync(fixture, "records-unentitled", "unrelated-role");

            // A positive control proves the restricted layer exists and both protocol aliases
            // are eligible. Neither protocol-facing index (100/200) is a canonical layer ID.
            await AssertLayerIdsAsync(fixture, entitled, StorageLayerIds);
            await AssertLayerIdsAsync(fixture, null, PublicLayerIds);
            await AssertLayerIdsAsync(fixture, unentitled, PublicLayerIds);
        }
        finally
        {
            provider.SetGraph(snapshot.Graph, schema: fixture.CurrentSchema);
        }
    }

    private static MetadataV2Publication ClonePublication(
        MetadataV2Publication source,
        string serviceId,
        MetadataV2PublicationType publicationType,
        int routeIndex)
    {
        var route = routeIndex.ToString(CultureInfo.InvariantCulture);
        return source with
        {
            Metadata = source.Metadata with { Id = $"pub-{serviceId}-{route}" },
            ServiceId = serviceId,
            PublicationType = publicationType,
            Identifier = new MetadataV2PublicationIdentifier { Value = route, IsNumeric = true },
            LayerIndex = routeIndex,
            ServiceLocalId = route,
            Path = null,
            IsPrimary = true
        };
    }

    private static async Task AssertLayerIdsAsync(WebAppFixture fixture, string? token, int[] expected)
    {
        var recordId = Uri.EscapeDataString($"service:{ServiceName}");
        var listUrl = $"/ogc/records/collections/honua-catalog/items?ids={recordId}&limit=100";
        var detailUrl = $"/ogc/records/collections/honua-catalog/items/{recordId}";
        foreach (var url in new[] { listUrl, detailUrl })
        {
            using var client = fixture.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Referrer = new Uri(Referer);
            }
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            using var document = JsonDocument.Parse(body);
            var record = document.RootElement;
            if (url == listUrl)
            {
                var records = record.GetProperty("features").EnumerateArray().ToArray();
                records.Should().ContainSingle();
                record = records[0];
            }
            record.GetProperty("id").GetString().Should().Be($"service:{ServiceName}");
            var ids = record.GetProperty("properties").GetProperty("layerIds")
                .EnumerateArray().Select(value => value.GetInt32()).ToArray();
            ids.Should().Equal(expected, "each visible canonical storage layer occurs once across protocol aliases");
            var links = record.GetProperty("links").EnumerateArray()
                .Select(link => link.GetProperty("href").GetString()).OfType<string>().ToArray();
            links.Should().Contain(link => link.EndsWith($"/{ServiceName}/FeatureServer", StringComparison.Ordinal));
            links.Should().Contain(link => link.EndsWith($"/{ServiceName}/MapServer", StringComparison.Ordinal));
        }
    }

    private static async Task<string> IssueAsync(WebAppFixture fixture, string principalId, string role)
        => (await fixture.GetService<IPortalTokenIssuer>().IssueAsync(
            new PortalTokenIssueRequest(principalId, principalId, TenantId: null, Roles: [role],
                PortalTokenClientType.Referer, Referer, DateTimeOffset.UtcNow.AddMinutes(30)),
            CancellationToken.None)).Token;
}
