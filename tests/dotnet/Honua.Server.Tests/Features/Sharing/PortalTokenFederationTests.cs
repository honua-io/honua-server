// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
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
/// A portal token has to become a server token before a protected GeoServices
/// resource will accept it. The server document names the owning portal, and
/// <c>generateToken</c> exchanges <c>token</c> plus <c>serverUrl</c> for a token
/// of the same principal bound to the requested referer and expiration.
/// </summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class PortalTokenFederationTests : IAsyncLifetime
{
    private const string AdminPassword = WebAppFixture.SharedAdminPassword;
    private const string ServiceId = "protected-features";
    private const string PortalReferer = "https://portal.example.com/maps/";
    private const string ServerReferer = "https://app.example.com/maps/";
    private const string TokenEndpoint = "/sharing/rest/generateToken";

    private readonly WebAppFixture _fixture;

    public PortalTokenFederationTests()
    {
        var policy = new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["admin"] };
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource(
                "res-protected",
                "Protected layer",
                MetadataV2ResourceType.FeatureDataset,
                fields:
                [
                    new MetadataV2Field
                    {
                        Name = "objectid",
                        Type = MetadataV2FieldType.Integer,
                        Nullable = false,
                        Alias = "Object ID",
                    },
                ],
                accessPolicy: policy,
                spatial: new MetadataV2ResourceSpatial
                {
                    SpatialReference = MetadataV2SpatialReference.Wgs84,
                    GeometryType = MetadataV2GeometryType.Point,
                })
            .AddStorageBinding("binding-protected", "res-protected", "protected.layers.0", storageLayerId: 0)
            .AddService(
                ServiceId,
                ServiceId,
                protocols: [ServiceProtocols.FeatureServer],
                accessPolicy: policy)
            .AddPublication(
                "pub-protected",
                ServiceId,
                "res-protected",
                layerIndex: 0,
                storageBindingId: "binding-protected",
                publicationType: MetadataV2PublicationType.EsriFeatureLayer)
            .Build();

        _fixture = new WebAppFixture()
            .ReplaceService<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph))
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
            });
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/info")]
    public async Task RestInfo_NamesTheOwningPortal()
    {
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync("/rest/info?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var owning = root.GetProperty("owningSystemUrl").GetString();
        var soap = root.GetProperty("soapUrl").GetString();
        owning.Should().NotBeNullOrWhiteSpace();
        soap.Should().Be(owning + "/services");
        root.GetProperty("authInfo").GetProperty("tokenServicesUrl").GetString()
            .Should().Be(owning + "/sharing/rest/generateToken");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /arcgisuris.xml")]
    public async Task PortalUriList_IsServedForTheSelfPortal()
    {
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync("/arcgisuris.xml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/xml");
        var root = XElement.Parse(await response.Content.ReadAsStringAsync());
        root.Name.LocalName.Should().Be("ArcGISOnlineURIList");
        root.Element("Name")!.Value.Should().Be("Honua");
        root.Element("Base")!.Value.Should().EndWith("/");
    }

    [IntegrationTest]
    [Endpoint("POST /sharing/rest/generateToken")]
    public async Task GenerateToken_ExchangesPortalTokenForServerUrl()
    {
        using var client = _fixture.CreateClient();
        var portalToken = await IssuePortalTokenAsync(client);
        var serverUrl = await ReadSoapUrlAsync(client);

        // The federated exchange: the portal token and the server URL stand in
        // for a username and password. The referer parameter is the binding the
        // issued server token must carry.
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = Form(
                ("token", portalToken),
                ("serverUrl", serverUrl),
                ("referer", PortalReferer),
                ("f", "json")),
        };
        request.Headers.Referrer = new Uri(PortalReferer);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(
            "the portal-token exchange must mint a server token, body: {0}", body);
        var serverToken = document.RootElement.GetProperty("token").GetString();
        serverToken.Should().NotBeNullOrWhiteSpace();
        serverToken.Should().NotBe(portalToken);
        document.RootElement.GetProperty("expires").GetInt64()
            .Should().BeGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        document.RootElement.GetProperty("ssl").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("POST /sharing/rest/generateToken")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task ExchangedToken_HonoursRefererAndExpirationAndReadsProtectedFeatureServer()
    {
        const int requestedMinutes = 12;
        using var client = _fixture.CreateClient();

        using (var anonymous = await client.GetAsync($"/rest/services/{ServiceId}/FeatureServer?f=json"))
        {
            await anonymous.AssertGeoServicesErrorAsync(499);
        }

        var portalToken = await IssuePortalTokenAsync(client);
        var serverUrl = await ReadSoapUrlAsync(client);
        var issuedAt = DateTimeOffset.UtcNow;
        using var exchange = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = Form(
                ("token", portalToken),
                ("serverUrl", serverUrl),
                ("referer", ServerReferer),
                ("expiration", requestedMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("f", "json")),
        };
        exchange.Headers.Referrer = new Uri(PortalReferer);
        using var exchangeResponse = await client.SendAsync(exchange);
        var exchangeBody = await exchangeResponse.Content.ReadAsStringAsync();
        exchangeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var exchanged = JsonDocument.Parse(exchangeBody);
        exchanged.RootElement.TryGetProperty("error", out _).Should().BeFalse(
            "exchange body: {0}", exchangeBody);
        var serverToken = exchanged.RootElement.GetProperty("token").GetString();
        serverToken.Should().NotBeNullOrWhiteSpace().And.NotBe(portalToken);
        var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(exchanged.RootElement.GetProperty("expires").GetInt64());
        (expiresAt - issuedAt).TotalMinutes.Should().BeApproximately(requestedMinutes, 1);

        using var read = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{ServiceId}/FeatureServer?f=json&token={Uri.EscapeDataString(serverToken!)}");
        read.Headers.Referrer = new Uri(ServerReferer);
        using var readResponse = await client.SendAsync(read);
        var readBody = await readResponse.Content.ReadAsStringAsync();
        readResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var service = JsonDocument.Parse(readBody);
        service.RootElement.TryGetProperty("error", out _).Should().BeFalse("protected read body: {0}", readBody);
        service.RootElement.GetProperty("layers").GetArrayLength().Should().BeGreaterThan(0);

        using var wrongReferer = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{ServiceId}/FeatureServer?f=json&token={Uri.EscapeDataString(serverToken!)}");
        wrongReferer.Headers.Referrer = new Uri(PortalReferer);
        using var rejected = await client.SendAsync(wrongReferer);
        await rejected.AssertGeoServicesErrorAsync(498);
    }

    [IntegrationTest]
    [Endpoint("POST /sharing/rest/generateToken")]
    public async Task GenerateToken_ExchangeRejectsAForeignServerUrl()
    {
        using var client = _fixture.CreateClient();
        var portalToken = await IssuePortalTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = Form(
                ("token", portalToken),
                ("serverUrl", "https://other.example.com/services"),
                ("referer", PortalReferer),
                ("f", "json")),
        };
        request.Headers.Referrer = new Uri(PortalReferer);
        using var response = await client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        await response.AssertGeoServicesErrorAsync(400);
        body.Should().Contain("not recognized");
        body.Should().NotContain("Username and password are required.");
    }

    private async Task<string> IssuePortalTokenAsync(HttpClient client)
    {
        using var response = await client.PostAsync(TokenEndpoint, Form(
            ("username", "admin"),
            ("password", AdminPassword),
            ("client", "referer"),
            ("referer", PortalReferer),
            ("f", "json")));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("token").GetString()!;
    }

    private static async Task<string> ReadSoapUrlAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/rest/info?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("soapUrl").GetString()!;
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] pairs)
        => new(pairs.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value)));
}
