// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Sharing;

/// <summary>
/// Integration tests for the read-only ArcGIS Portal/Sharing REST surface (#1243):
/// <c>info</c>, <c>portals/self</c>, <c>community/self</c>, <c>search</c>, and
/// <c>content/items/{id}</c>. Verifies the Esri response shapes, RBAC-scoped
/// visibility through <c>IPortalItemProjector</c>, paging, and entitlement gating.
/// </summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class SharingRestReadTests : IAsyncLifetime
{
    private const string AdminPassword = WebAppFixture.SharedAdminPassword;
    private const string PublicServiceId = "public-svc";
    private const string PrivateServiceId = "private-svc";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;

    public SharingRestReadTests()
    {
        _fixture = CreateFixture();
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    /// <summary>
    /// Builds a fixture with a fixed Metadata v2 graph: one anonymously-readable
    /// FeatureServer, one role-restricted FeatureServer, and one non-Esri (OGC)
    /// service that must never project to a portal item.
    /// </summary>
    private static WebAppFixture CreateFixture()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddService(
                PublicServiceId,
                "Public Roads",
                protocols: [ServiceProtocols.FeatureServer],
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddService(
                PrivateServiceId,
                "Private Parcels",
                protocols: [ServiceProtocols.FeatureServer],
                accessPolicy: new AccessPolicy { AllowedRoles = ["parcel-admin"] })
            .AddService(
                "ogc-only",
                "Coverage Catalog",
                protocols: ["OgcApiFeatures"],
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .Build();

        var fixture = new WebAppFixture()
            .ReplaceService<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph))
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
            });

        return fixture;
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/info")]
    public async Task Info_Anonymous_ReturnsAuthInfoShape()
    {
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync("/sharing/rest/info?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        // Honua does not advertise an ArcGIS Portal/Server version (see NoArcGisServerVersionTests).
        root.TryGetProperty("currentVersion", out _).Should().BeFalse();
        root.TryGetProperty("fullVersion", out _).Should().BeFalse();
        var authInfo = root.GetProperty("authInfo");
        authInfo.GetProperty("isTokenBasedSecurity").GetBoolean().Should().BeTrue();
        authInfo.GetProperty("tokenServicesUrl").GetString().Should().EndWith("/sharing/rest/generateToken");
    }

    [IntegrationTheory]
    [InlineData("http", "")]
    [InlineData("https", "")]
    [InlineData("https", "/arcgis")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /sharing/rest/info")]
    public async Task Info_Anonymous_UsesActualPortalRoot(string scheme, string prefix)
    {
        using var client = _fixture.CreateClient();
        client.BaseAddress = new Uri($"{scheme}://localhost");
        using var response = await client.GetAsync(prefix + "/sharing/rest/info?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var expectedRoot = $"{scheme}://localhost{prefix}";
        document.RootElement.GetProperty("owningSystemUrl").GetString().Should().Be(expectedRoot);
        document.RootElement.GetProperty("authInfo").GetProperty("tokenServicesUrl")
            .GetString().Should().Be(expectedRoot + "/sharing/rest/generateToken");
        document.RootElement.TryGetProperty("currentVersion", out _).Should().BeFalse();
        document.RootElement.TryGetProperty("fullVersion", out _).Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData("http", "")]
    [InlineData("https", "")]
    [InlineData("https", "/arcgis")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /arcgisuris.xml")]
    public async Task PortalUriList_Anonymous_UsesActualOriginAndPathBase(string scheme, string prefix)
    {
        using var client = _fixture.CreateClient();
        client.BaseAddress = new Uri($"{scheme}://localhost");
        using var response = await client.GetAsync(prefix + "/arcgisuris.xml");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/xml");
        var root = XElement.Parse(await response.Content.ReadAsStringAsync());
        var expectedBase = $"{scheme}://localhost{prefix}/";
        root.Name.LocalName.Should().Be("ArcGISOnlineURIList");
        root.Element("Name")!.Value.Should().Be("Honua");
        root.Element("Base")!.Value.Should().Be(expectedBase);
        root.Element("PingTest")!.Value.Should().Be(expectedBase + "sharing/rest/info?f=json");
        (root.Element("Secure")?.Value).Should().Be(scheme == "https" ? expectedBase : null);
        root.Elements().Select(element => element.Name.LocalName)
            .Should().NotContain(["NewAccount", "ForgottenPassword", "Update", "BasemapQuery"]);

        using var request = new HttpRequestMessage(HttpMethod.Head, prefix + "/arcgisuris.xml");
        using var head = await client.SendAsync(request);
        head.StatusCode.Should().Be(HttpStatusCode.OK);
        (await head.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/info")]
    public async Task GeoServicesInfo_AdvertisesTokenAuthentication()
    {
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync("/rest/info?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var owning = doc.RootElement.GetProperty("owningSystemUrl").GetString();
        owning.Should().NotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("soapUrl").GetString().Should().Be(owning + "/services");
        var authInfo = doc.RootElement.GetProperty("authInfo");
        authInfo.GetProperty("isTokenBasedSecurity").GetBoolean().Should().BeTrue();
        authInfo.GetProperty("tokenServicesUrl").GetString().Should().Be(owning + "/sharing/rest/generateToken");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/portals/self")]
    public async Task PortalsSelf_Anonymous_OmitsUser()
    {
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync("/sharing/rest/portals/self?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("name").GetString().Should().NotBeNullOrWhiteSpace();
        root.TryGetProperty("user", out var user).Should().BeTrue();
        (user.ValueKind == JsonValueKind.Null).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/community/self")]
    public async Task CommunitySelf_Anonymous_Returns401()
    {
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync("/sharing/rest/community/self?f=json");

        await response.AssertGeoServicesErrorAsync(401, 499);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/search")]
    public async Task Search_Anonymous_ReturnsOnlyPublicItemsWithPagingShape()
    {
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync("/sharing/rest/search?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadSearchAsync(response);

        payload.Total.Should().Be(1);
        payload.Start.Should().Be(1);
        payload.Num.Should().Be(1);
        payload.NextStart.Should().Be(-1);
        payload.Results.Should().ContainSingle();
        payload.Results[0].Id.Should().Be(PublicServiceId);
        payload.Results[0].Access.Should().Be("public");
        payload.Results[0].Type.Should().Be("Feature Service");
        // The role-restricted and non-Esri services must never surface to anon.
        payload.Results.Should().NotContain(r => r.Id == PrivateServiceId);
        payload.Results.Should().NotContain(r => r.Id == "ogc-only");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/search")]
    public async Task Search_WithPaging_HonorsStartAndNum()
    {
        using var client = _fixture.CreateClient();
        // num=0 is invalid -> default; request the second page of a single-item set.
        using var response = await client.GetAsync("/sharing/rest/search?f=json&start=2&num=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadSearchAsync(response);
        payload.Total.Should().Be(1);
        payload.Num.Should().Be(0);
        payload.Results.Should().BeEmpty();
        payload.NextStart.Should().Be(-1);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/search")]
    public async Task Search_WithTypeQualifier_FiltersByType()
    {
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync("/sharing/rest/search?f=json&q=type:Feature");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadSearchAsync(response);
        payload.Results.Should().NotBeEmpty();
        payload.Results.Should().OnlyContain(r => r.Type == "Feature Service");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/content/items/{id}")]
    public async Task ContentItem_PublicItem_RoundTrips()
    {
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync($"/sharing/rest/content/items/{PublicServiceId}?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("id").GetString().Should().Be(PublicServiceId);
        root.GetProperty("title").GetString().Should().Be("Public Roads");
        root.GetProperty("url").GetString().Should().Contain("/rest/services/");
        root.GetProperty("url").GetString().Should().EndWith("/FeatureServer");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/content/items/{id}/data")]
    public async Task ContentItemData_PublicItem_RoundTrips()
    {
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync($"/sharing/rest/content/items/{PublicServiceId}/data?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("id").GetString().Should().Be(PublicServiceId);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/content/items/{id}")]
    public async Task ContentItem_RoleRestrictedItem_AnonymousGets404()
    {
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync($"/sharing/rest/content/items/{PrivateServiceId}?f=json");

        await response.AssertGeoServicesErrorAsync(404);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // Esri-shaped error envelope: { error: { code, message, details } }.
        var error = doc.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(404);
        error.TryGetProperty("message", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/content/items/{id}")]
    public async Task ContentItem_UnknownId_Returns404()
    {
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync("/sharing/rest/content/items/does-not-exist?f=json");

        await response.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/search")]
    public async Task ReadSurface_WhenEntitlementDisabled_Returns404()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(
                new TestMetadataV2GraphBuilder()
                    .AddService(PublicServiceId, "Public Roads",
                        protocols: [ServiceProtocols.FeatureServer],
                        accessPolicy: new AccessPolicy { AllowAnonymous = true })
                    .Build()))
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
                // Disable the read surface -> whole surface 404s.
                builder.UseSetting("Sharing:ReadSurface:Enabled", "false");
            });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var searchResponse = await client.GetAsync("/sharing/rest/search?f=json");
            using var infoResponse = await client.GetAsync("/sharing/rest/info?f=json");
            using var itemResponse = await client.GetAsync($"/sharing/rest/content/items/{PublicServiceId}?f=json");
            using var uriResponse = await client.GetAsync("/arcgisuris.xml");

            await searchResponse.AssertGeoServicesErrorAsync(404);
            await infoResponse.AssertGeoServicesErrorAsync(404);
            await itemResponse.AssertGeoServicesErrorAsync(404);
            uriResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<SearchPayload> ReadSearchAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<SearchPayload>(json, JsonOptions)
            ?? throw new InvalidOperationException("Empty search response body.");
    }

    private sealed record SearchPayload
    {
        public int Total { get; init; }

        public int Start { get; init; }

        public int Num { get; init; }

        public int NextStart { get; init; }

        public ItemPayload[] Results { get; init; } = Array.Empty<ItemPayload>();
    }

    private sealed record ItemPayload
    {
        public string Id { get; init; } = string.Empty;

        public string Type { get; init; } = string.Empty;

        public string Access { get; init; } = string.Empty;
    }
}
