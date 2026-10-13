// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geocoding;

/// <summary>
/// The root-mounted GeoServices tree and the <c>/arcgis</c> instance-path alias must answer the
/// same locator discovery sequence: catalog root, folder, service resource, and REST info.
/// A client resolves the geocode service from those documents on first contact, so both mounts
/// publish the conventional instance path in service and SOAP URLs. Portal discovery
/// retains each actual request root unless an explicit public base URL overrides it.
/// </summary>
[Protocol(TestProtocols.Geocoding)]
public sealed class Issue5536RootMountedGeocodeDiscoveryTests
{
    private const string LocatorName = "World";
    private const string InstanceSegment = "/arcgis";

    [IntegrationTheory]
    [InlineData(null)]
    [InlineData("https://gis.example.test")]
    [InlineData("https://gis.example.test/arcgis")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    [Endpoint("GET /rest/services/{folderName}")]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer")]
    [Endpoint("GET /rest/info")]
    [Endpoint("GET /sharing/rest/info")]
    public async Task RootMountedTree_MatchesInstancePathDiscovery(string? publicBaseUrl)
    {
        using var factory = CreateFactory(publicBaseUrl);
        using var client = factory.CreateClient();

        var requestOrigin = client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        var root = await WalkAsync(client, string.Empty, publicBaseUrl ?? requestOrigin);
        var alias = await WalkAsync(client, InstanceSegment, publicBaseUrl ?? requestOrigin + InstanceSegment);

        root.CatalogUrl.Should().Be(alias.CatalogUrl);
        root.FolderUrl.Should().Be(alias.FolderUrl);
        root.SoapUrl.Should().Be(alias.SoapUrl);
        root.SecureSoapUrl.Should().Be(alias.SecureSoapUrl);
        root.ServiceBody.Should().Be(alias.ServiceBody);

        root.CatalogUrl.Should().EndWith($"{InstanceSegment}/rest/services/{LocatorName}/GeocodeServer");
        root.FolderUrl.Should().Be(root.CatalogUrl);
        root.SoapUrl.Should().EndWith($"{InstanceSegment}/services");

        root.CurrentVersion.Should().Be(10.8);
        alias.CurrentVersion.Should().Be(10.8);
        root.HasFullVersion.Should().BeFalse();
        alias.HasFullVersion.Should().BeFalse();

        if (publicBaseUrl is null)
        {
            root.SecureSoapUrl.Should().BeNull();
        }
        else
        {
            root.SecureSoapUrl.Should().Be(root.SoapUrl);
            var serviceRoot = publicBaseUrl.EndsWith(InstanceSegment, StringComparison.Ordinal)
                ? publicBaseUrl
                : publicBaseUrl + InstanceSegment;
            root.CatalogUrl.Should().Be($"{serviceRoot}/rest/services/{LocatorName}/GeocodeServer");
        }

        await AssertReachableAsync(client, root.CatalogUrl);
        await AssertReachableAsync(client, new Uri(root.SoapUrl).AbsolutePath);
    }

    private static WebApplicationFactory<Program> CreateFactory(string? publicBaseUrl)
    {
        // The services directory resolves a metadata graph and a raster store before it
        // appends the locator. The shared RBAC host supplies an in-memory graph; a raster
        // substitute keeps that read off a database. The locator entry is still the one
        // this sequence resolves.
        var factory = ServiceRbacTestFixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<IRasterStore>();
            services.AddSingleton(Substitute.For<IRasterStore>());
        });

        if (publicBaseUrl is null)
        {
            return factory;
        }

        return factory.WithWebHostBuilder(builder => builder.UseSetting("Public:BaseUrl", publicBaseUrl));
    }

    private static async Task<Discovery> WalkAsync(HttpClient client, string prefix, string expectedPortalRoot)
    {
        using var catalogResponse = await client.GetAsync($"{prefix}/rest/services?f=json");
        catalogResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"{prefix}/rest/services is the catalog root");
        using var catalog = JsonDocument.Parse(await catalogResponse.Content.ReadAsStringAsync());
        var catalogEntry = GeocodeEntry(catalog.RootElement, $"{prefix}/rest/services");

        using var folderResponse = await client.GetAsync($"{prefix}/rest/services/{LocatorName}?f=json");
        folderResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"{prefix}/rest/services/{LocatorName} is the locator folder");
        using var folder = JsonDocument.Parse(await folderResponse.Content.ReadAsStringAsync());
        var folderEntry = GeocodeEntry(folder.RootElement, $"{prefix}/rest/services/{LocatorName}");

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, $"{prefix}/rest/services/{LocatorName}/GeocodeServer?f=json");
        using var headResponse = await client.SendAsync(headRequest);
        using var serviceResponse = await client.GetAsync($"{prefix}/rest/services/{LocatorName}/GeocodeServer?f=json");
        headResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        serviceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var serviceBody = await serviceResponse.Content.ReadAsStringAsync();
        (await headResponse.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        headResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        using var service = JsonDocument.Parse(serviceBody);
        service.RootElement.GetProperty("locatorProperties").GetProperty("LocatorName").GetString().Should().Be(LocatorName);

        using var infoResponse = await client.GetAsync($"{prefix}/rest/info?f=json");
        infoResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"{prefix}/rest/info is the REST root");
        using var info = JsonDocument.Parse(await infoResponse.Content.ReadAsStringAsync());
        var infoRoot = info.RootElement;
        infoRoot.GetProperty("owningSystemUrl").GetString().Should().Be(expectedPortalRoot);
        var tokenServicesUrl = infoRoot.GetProperty("authInfo").GetProperty("tokenServicesUrl").GetString();
        tokenServicesUrl.Should().Be(expectedPortalRoot + "/sharing/rest/generateToken");

        // The portal identity must agree across the server and Sharing documents at
        // this mount. The conventional SOAP alias is not an implicit portal root.
        using var sharingResponse = await client.GetAsync($"{prefix}/sharing/rest/info?f=json");
        sharingResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var sharing = JsonDocument.Parse(await sharingResponse.Content.ReadAsStringAsync());
        sharing.RootElement.GetProperty("owningSystemUrl").GetString().Should().Be(expectedPortalRoot);
        sharing.RootElement.GetProperty("authInfo").GetProperty("tokenServicesUrl")
            .GetString().Should().Be(tokenServicesUrl);

        var secure = infoRoot.GetProperty("secureSoapUrl");
        return new Discovery(
            catalogEntry,
            folderEntry,
            serviceBody,
            infoRoot.GetProperty("soapUrl").GetString()!,
            secure.ValueKind == JsonValueKind.Null ? null : secure.GetString(),
            infoRoot.GetProperty("currentVersion").GetDouble(),
            infoRoot.TryGetProperty("fullVersion", out _));
    }

    private static string GeocodeEntry(JsonElement directory, string step)
    {
        var body = directory.GetRawText();
        directory.TryGetProperty("fullVersion", out _).Should().BeFalse($"{step} must not advertise fullVersion. Body: {body}");
        directory.TryGetProperty("services", out var services).Should().BeTrue($"{step} catalog document: {body}");
        var entry = services.EnumerateArray()
            .Where(service => service.TryGetProperty("type", out var type) && type.GetString() == "GeocodeServer")
            .Should().ContainSingle($"{step} lists the configured locator. Body: {body}")
            .Which;
        entry.GetProperty("name").GetString().Should().Be(LocatorName);
        return entry.GetProperty("url").GetString()!;
    }

    private static async Task AssertReachableAsync(HttpClient client, string urlOrPath)
    {
        var path = Uri.TryCreate(urlOrPath, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : urlOrPath;
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, path);
        using var head = await client.SendAsync(headRequest);
        using var get = await client.GetAsync(path);
        head.StatusCode.Should().Be(HttpStatusCode.OK, path);
        get.StatusCode.Should().Be(HttpStatusCode.OK, path);
    }

    private sealed record Discovery(
        string CatalogUrl,
        string FolderUrl,
        string ServiceBody,
        string SoapUrl,
        string? SecureSoapUrl,
        double CurrentVersion,
        bool HasFullVersion);
}
