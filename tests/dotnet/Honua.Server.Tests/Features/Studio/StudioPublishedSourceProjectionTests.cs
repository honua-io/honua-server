// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Server.Features.Studio.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Studio;

/// <summary>Real published reads keep audience metadata separate from publication authority.</summary>
[Collection("Database")]
[Protocol(TestProtocols.Studio)]
[Operation(Operations.StudioLifecycle)]
public sealed class StudioPublishedSourceProjectionTests : IAsyncLifetime
{
    private const string ServiceId = "internal/service !'()*";
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("Licensing:Mode", "Disabled");
        })
        .ConfigureServices(services =>
        {
            services.RemoveAll<IStudioPackageStore>();
            services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
        });
    private HttpClient _admin = null!;
    private HttpClient _anonymous = null!;
    private MetadataV2Graph _graph = null!;
    private TestMetadataV2GraphProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _admin = _fixture.CreateAdminClient();
        _anonymous = _fixture.CreateClient();
        _provider = (TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>();
        var builder = new TestMetadataV2GraphBuilder()
            .AddService(ServiceId, "ActualParcels", protocols: [ServiceProtocols.FeatureServer]);
        foreach (var id in new[] { 7, 8, 9, 10 })
        {
            builder.AddResource($"resource-{id}", $"Parcels-{id}",
                accessPolicy: new AccessPolicy { AllowAnonymous = id != 8, AllowedRoles = ["admin"] },
                spatial: new MetadataV2ResourceSpatial { GeometryType = MetadataV2GeometryType.GeometryCollection })
                .AddPublication($"publication-{id}", ServiceId, $"resource-{id}", layerIndex: id);
        }
        var graph = builder.Build();
        _graph = graph with
        {
            Resources = graph.Resources.Select(resource => resource.Metadata.Id == "resource-9"
                ? resource with { Metadata = resource.Metadata with { Tenant = "another-tenant" } }
                : resource).ToArray(),
        };
        SetGraph(_graph);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("GET /api/v1/studio/published/{*route}")]
    public async Task Projection_UsesCurrentReaderAndActualLocator_WithoutUnrelatedOrCrossTenantSources(bool administrator)
    {
        var url = await PublishAsync(7, 8, 9);
        using var response = await (administrator ? _admin : _anonymous).GetAsync(url + "?includeSources=true");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var sources = document.RootElement.GetProperty("data").GetProperty("sourceCatalog").EnumerateArray().ToArray();
        sources.Select(source => source.GetProperty("id").GetString()).Should().BeEquivalentTo(
            administrator ? new[] { SourceId(7), SourceId(8) } : new[] { SourceId(7) });
        var first = sources.Single(source => source.GetProperty("id").GetString() == SourceId(7));
        first.GetProperty("id").GetString().Should().Be("internal%2Fservice%20!'()*/7");
        first.GetProperty("geometryType").GetString().Should().Be("GeometryCollection");
        first.GetProperty("locator").GetProperty("serviceId").GetString().Should().Be(ServiceId);
        first.GetProperty("locator").GetProperty("layerId").GetInt32().Should().Be(7);
        first.GetProperty("locator").GetProperty("url").GetString().Should().EndWith("/rest/services/ActualParcels/FeatureServer");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/published/{*route}")]
    public async Task DefaultRead_StaysUnchanged_AndProjectionDoesNotChangeTheSealedVersion()
    {
        var url = await PublishAsync(7);
        using var ordinary = await ReadAsync(_anonymous, url);
        using var projected = await ReadAsync(_anonymous, url + "?includeSources=true");
        var original = ordinary.RootElement.GetProperty("data");
        var augmented = projected.RootElement.GetProperty("data");
        original.TryGetProperty("sourceCatalog", out _).Should().BeFalse();
        augmented.GetProperty("contentHash").GetString().Should().Be(original.GetProperty("contentHash").GetString());
        augmented.GetProperty("versionId").GetString().Should().Be(original.GetProperty("versionId").GetString());
        JsonElement.DeepEquals(original.GetProperty("envelope"), augmented.GetProperty("envelope")).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/published/{*route}")]
    public async Task Projection_RechecksCurrentSourceAccess_AfterPublicAccessIsRevoked()
    {
        var url = await PublishAsync(7);
        using var before = await ReadAsync(_anonymous, url + "?includeSources=true");
        before.RootElement.GetProperty("data").GetProperty("sourceCatalog").GetArrayLength().Should().Be(1);
        SetGraph(_graph with
        {
            Revision = _graph.Revision + 1,
            Resources = _graph.Resources.Select(resource => resource.Metadata.Id == "resource-7"
                ? resource with { AccessPolicy = new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["admin"] } }
                : resource).ToArray(),
        });
        using var after = await ReadAsync(_anonymous, url + "?includeSources=true");
        after.RootElement.GetProperty("data").GetProperty("sourceCatalog").GetArrayLength().Should().Be(0);
        using var owner = await ReadAsync(_admin, url + "?includeSources=true");
        owner.RootElement.GetProperty("data").GetProperty("sourceCatalog").GetArrayLength().Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/published/{*route}")]
    public async Task Projection_RefusesConflictingResourcesForOneSource_WithoutExposingDetails()
    {
        var url = await PublishAsync(7);
        var conflicting = _graph.Publications.Single(publication => publication.Metadata.Id == "publication-10") with
        {
            LayerIndex = 7,
            Identifier = new MetadataV2PublicationIdentifier { Value = "7", IsNumeric = true },
        };
        SetGraph(_graph with
        {
            Revision = _graph.Revision + 1,
            Publications = _graph.Publications.Where(publication => publication.Metadata.Id != "publication-10")
                .Append(conflicting).ToArray(),
        });
        using var response = await _anonymous.GetAsync(url + "?includeSources=true");
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("resource-10").And.NotContain("InvalidOperationException");
        using var ordinary = await ReadAsync(_anonymous, url);
        ordinary.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/published/{*route}")]
    public async Task PrivatePublication_StillDeniesAnonymousProjection()
    {
        var url = await PublishAsync([7], "personal");
        using var response = await _anonymous.GetAsync(url + "?includeSources=true");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/published/{*route}")]
    public async Task LegacyAlias_CannotRetargetToTheOnlyLayerVisibleToTheReader()
    {
        SetGraph(_graph with
        {
            Resources = _graph.Resources.Select(resource => resource.Metadata.Id == "resource-7"
                ? resource
                : resource with { AccessPolicy = new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["admin"] } }).ToArray(),
        });
        var url = await PublishAsync([7], "public", legacyAlias: true);
        using var response = await ReadAsync(_anonymous, url + "?includeSources=true");
        response.RootElement.GetProperty("data").GetProperty("sourceCatalog").GetArrayLength().Should().Be(0);
    }

    private void SetGraph(MetadataV2Graph graph) => _provider.SetGraph(graph, schema: _fixture.CurrentSchema);

    private static string SourceId(int layerId) => StudioCatalogSourceIdentity.Create(ServiceId, layerId);

    private Task<string> PublishAsync(params int[] layers) => PublishAsync(layers, "public");

    private async Task<string> PublishAsync(int[] layers, string visibility, bool legacyAlias = false)
    {
        var key = $"audience-{Guid.NewGuid():N}";
        var body = JsonSerializer.SerializeToElement(new
        {
            mapPackageId = key,
            format = "honua_map_package.v1",
            status = "Ready",
            createdAt = "2026-10-20T00:00:00Z",
            layers = layers.Select(id => new { id = $"layer-{id}", sourceId = legacyAlias ? ServiceId : SourceId(id), type = "fill", visible = true }),
        });
        var request = new CreateStudioPackageDraftRequest
        {
            PackageKey = key,
            Envelope = new StudioPackageEnvelope
            {
                Family = StudioPackageFamily.Map,
                SchemaVersion = "1.0",
                Format = "honua_map_package.v1",
                Body = body,
            },
        };
        using var create = await _admin.PostAsync("/api/v1/studio/package-drafts", JsonContent(
            JsonSerializer.Serialize(request, StudioApiJsonContext.Default.CreateStudioPackageDraftRequest)));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        using var draft = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var draftId = draft.RootElement.GetProperty("data").GetProperty("draftId").GetString();
        using var save = await _admin.PostAsync($"/api/v1/studio/package-drafts/{draftId}/content-versions", JsonContent("{}"));
        save.StatusCode.Should().Be(HttpStatusCode.Created);
        using var version = JsonDocument.Parse(await save.Content.ReadAsStringAsync());
        var data = version.RootElement.GetProperty("data");
        var itemId = data.GetProperty("itemId").GetString();
        var versionId = data.GetProperty("versionId").GetString();
        var route = "/maps/" + key;
        using var publish = await _admin.PostAsync($"/api/v1/studio/content-items/{itemId}/versions/{versionId}/publish-requests",
            JsonContent(JsonSerializer.Serialize(new { intent = new { route, visibility } })));
        publish.StatusCode.Should().Be(HttpStatusCode.Created);
        using var publication = JsonDocument.Parse(await publish.Content.ReadAsStringAsync());
        publication.RootElement.GetProperty("data").GetProperty("status").GetString().Should().BeEquivalentTo("Accepted");
        return StudioPublishedRoutes.BuildActiveUrl(route);
    }

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonDocument> ReadAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
