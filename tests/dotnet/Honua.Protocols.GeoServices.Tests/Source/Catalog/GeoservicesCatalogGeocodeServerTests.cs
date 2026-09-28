// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Protocols.GeoServices.Catalog;
using Honua.TestKit.Attributes;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

[Protocol(TestProtocols.GeoservicesCatalog)]
public sealed class GeoservicesCatalogGeocodeServerTests
{
    [IntegrationTheory]
    [InlineData("World", "GetServiceDescriptions")]
    [InlineData("City Locator", "GetServiceDescriptionsEx")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    [Endpoint("POST /services")]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer")]
    [Endpoint("GET /rest/services/GeocodeServer")]
    public async Task Catalogs_ConfiguredLocator_AdvertiseCanonicalReachableMetadata(string locatorName, string operation)
    {
        const string baseUrl = "https://gis.example.test/arcgis";
        using var factory = CreateFactory(locatorName, baseUrl: baseUrl);
        using var client = factory.CreateClient();
        using var rest = await ReadRestAsync(client);
        var entry = rest.RootElement.GetProperty("services").EnumerateArray()
            .Where(service => service.GetProperty("type").GetString() == "GeocodeServer")
            .Should().ContainSingle().Which;
        var expectedUrl = $"{baseUrl}/rest/services/{Uri.EscapeDataString(locatorName)}/GeocodeServer";
        entry.GetProperty("name").GetString().Should().Be(locatorName);
        entry.GetProperty("url").GetString().Should().Be(expectedUrl);

        var soap = await ReadSoapAsync(client, operation);
        var description = soap.Descendants().Where(element => element.Name.LocalName == "ServiceDescription")
            .Where(element => ChildValue(element, "Type") == "GeocodeServer")
            .Should().ContainSingle().Which;
        ChildValue(description, "Name").Should().Be(locatorName);
        ChildValue(description, "RestUrl").Should().Be(expectedUrl);
        ChildValue(description, "Url").Should().Be(expectedUrl);

        // A deployment proxy removes its advertised /arcgis prefix before forwarding.
        var upstreamPath = new Uri(expectedUrl).PathAndQuery["/arcgis".Length..];
        using var metadataResponse = await client.GetAsync(upstreamPath);
        metadataResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync());
        metadata.RootElement.GetProperty("locatorProperties").GetProperty("LocatorName").GetString().Should().Be(locatorName);
        ChildValue(description, "Capabilities").Should().Be(metadata.RootElement.GetProperty("capabilities").GetString());

        using var aliasResponse = await client.GetAsync("/rest/services/GeocodeServer?f=json");
        aliasResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var alias = JsonDocument.Parse(await aliasResponse.Content.ReadAsStringAsync());
        alias.RootElement.GetProperty("locatorProperties").GetProperty("LocatorName").GetString().Should().Be(locatorName);
    }

    [IntegrationTheory]
    [InlineData(false, true, 404)]
    [InlineData(true, false, 400)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    [Endpoint("POST /services")]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer")]
    public async Task Catalogs_UnavailableLocator_OmitUnreachableService(bool enabled, bool configured, int expectedCode)
    {
        using var factory = CreateFactory(enabled: enabled, configured: configured);
        using var client = factory.CreateClient();
        using var rest = await ReadRestAsync(client);
        rest.RootElement.GetProperty("services").EnumerateArray()
            .Should().NotContain(service => service.GetProperty("type").GetString() == "GeocodeServer");
        var soap = await ReadSoapAsync(client);
        soap.Descendants().Where(element => element.Name.LocalName == "Type")
            .Should().NotContain(element => element.Value == "GeocodeServer");
        using var metadata = await client.GetAsync("/rest/services/World/GeocodeServer?f=json");
        await metadata.AssertGeoServicesErrorAsync(expectedCode);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    [Endpoint("POST /services")]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer")]
    public async Task Catalogs_ProviderFactoryFails_PreserveOtherServiceDiscovery()
    {
        using var factory = CreateFactory(providerFails: true);
        using var client = factory.CreateClient();
        using var rest = await ReadRestAsync(client);
        var entries = rest.RootElement.GetProperty("services").EnumerateArray().ToArray();
        entries.Should().Contain(entry => entry.GetProperty("type").GetString() == "FeatureServer");
        entries.Should().NotContain(entry => entry.GetProperty("type").GetString() == "GeocodeServer");
        var soap = await ReadSoapAsync(client);
        var types = soap.Descendants().Where(element => element.Name.LocalName == "Type").Select(element => element.Value);
        types.Should().Contain("FeatureServer").And.NotContain("GeocodeServer");
        using var metadata = await client.GetAsync("/rest/services/World/GeocodeServer?f=json");
        await metadata.AssertGeoServicesErrorAsync(500);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    [Endpoint("POST /services")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task Catalogs_PublicLocatorAndRestrictedFeatures_ExposeOnlyLocatorToAnonymousCaller()
    {
        using var factory = CreateFactory(restrictedFeatures: true);
        using var client = factory.CreateClient();
        using var rest = await ReadRestAsync(client);
        rest.RootElement.GetProperty("services").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("type").GetString().Should().Be("GeocodeServer");
        var soap = await ReadSoapAsync(client);
        soap.Descendants().Where(element => element.Name.LocalName == "ServiceDescription")
            .Should().ContainSingle().Which.Elements().Single(element => element.Name.LocalName == "Type")
            .Value.Should().Be("GeocodeServer");
        using var feature = await client.GetAsync($"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer?f=json");
        await feature.AssertGeoServicesErrorAsync(499);
    }

    [IntegrationTheory]
    [InlineData(null, 499)]
    [InlineData("other-role", 403)]
    [InlineData("catalog-reader", 0)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{folderName}")]
    public async Task ServiceFolder_PublicLocator_PreservesProtectedFolderAuthorization(string? role, int expectedError)
    {
        using var factory = CreateFactory(restrictedFeatures: true);
        using var client = role is null ? factory.CreateClient() : ServiceRbacTestFixture.CreateClient(factory, role);
        using var folder = await client.GetAsync($"/rest/services/{ServiceRbacTestFixture.AlphaService}?f=json");
        if (expectedError != 0)
        {
            await folder.AssertGeoServicesErrorAsync(expectedError);
        }
        else
        {
            folder.StatusCode.Should().Be(HttpStatusCode.OK);
            using var directory = JsonDocument.Parse(await folder.Content.ReadAsStringAsync());
            directory.RootElement.GetProperty("services").EnumerateArray().Should().NotBeEmpty()
                .And.OnlyContain(entry => entry.GetProperty("name").GetString() == ServiceRbacTestFixture.AlphaService);
        }

        using var missing = await client.GetAsync("/rest/services/missing-folder?f=json");
        await missing.AssertGeoServicesErrorAsync(404);
        using var publicFolder = await client.GetAsync("/rest/services/World?f=json");
        publicFolder.StatusCode.Should().Be(HttpStatusCode.OK);
        using var locator = JsonDocument.Parse(await publicFolder.Content.ReadAsStringAsync());
        locator.RootElement.GetProperty("services").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("type").GetString().Should().Be("GeocodeServer");
    }

    [IntegrationTheory]
    [InlineData(false, true, "ReverseGeocode")]
    [InlineData(true, false, "Geocode")]
    [InlineData(false, false, "")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services")]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer")]
    public async Task Catalogs_ProviderOperationFlags_MatchMetadataCapabilities(bool forward, bool reverse, string expected)
    {
        using var factory = CreateFactory(providerCapabilities: new GeocodeProviderCapabilities(
            SupportsForwardGeocode: forward, SupportsReverseGeocode: reverse));
        using var client = factory.CreateClient();
        var soap = await ReadSoapAsync(client);
        var description = soap.Descendants().Where(element => element.Name.LocalName == "ServiceDescription")
            .Single(element => ChildValue(element, "Type") == "GeocodeServer");
        ChildValue(description, "Capabilities").Should().Be(expected);
        using var response = await client.GetAsync("/rest/services/World/GeocodeServer?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        metadata.RootElement.GetProperty("capabilities").GetString().Should().Be(expected);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task FeatureMapProjection_ConfiguredLocator_RemainsLimitedToFeatureAndMapServices()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("gis.example.test");
        context.Request.PathBase = "/arcgis";
        var snapshot = await scope.ServiceProvider.GetRequiredService<IMetadataV2GraphProvider>().GetCurrentAsync();

        var entries = await GeoServicesCatalogProjection.ReadFeatureMapAsync(context, snapshot);

        entries.Should().HaveCount(4).And.OnlyContain(entry => entry.Type == "FeatureServer" || entry.Type == "MapServer");
        // Without Public:BaseUrl, link generation uses the safe local origin, never the request Host.
        entries.Should().OnlyContain(entry => entry.Url.StartsWith("https://localhost/arcgis/rest/services/", StringComparison.Ordinal));
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string locatorName = "World", bool enabled = true, bool configured = true,
        bool restrictedFeatures = false, string? baseUrl = null, bool providerFails = false,
        GeocodeProviderCapabilities? providerCapabilities = null)
    {
        var policy = ServiceRbacTestFixture.CreateServiceMetadata(
            allowAnonymous: !restrictedFeatures, readRoles: restrictedFeatures ? ["catalog-reader"] : null);
        var provider = Substitute.For<IGeocodeProvider>();
        provider.Name.Returns("catalog-test");
        provider.Capabilities.Returns(providerCapabilities ?? new GeocodeProviderCapabilities { SupportsSuggest = true, SupportsBatch = true });
        var registry = Substitute.For<IGeocodeProviderRegistry>();
        registry.GetProvider("catalog-test").Returns(_ => providerFails
            ? throw new InvalidOperationException("Configured provider cannot initialize.")
            : configured ? provider : null);
        return ServiceRbacTestFixture.CreateFactory(
            () => new RbacTestLayerCatalog(
                alphaServiceMetadata: policy, betaServiceMetadata: policy,
                alphaLayerMetadata: policy, betaLayerMetadata: policy),
            services =>
            {
                services.AddSingleton(Substitute.For<IRasterStore>());
                services.RemoveAll<IGeocodeProviderRegistry>();
                services.AddSingleton(registry);
            }).WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Geocoding:Enabled"] = enabled ? "true" : "false",
                    ["Geocoding:DefaultProvider"] = "catalog-test",
                    ["Geocoding:LocatorName"] = locatorName,
                    ["Public:BaseUrl"] = baseUrl
                })));
    }

    private static async Task<JsonDocument> ReadRestAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/rest/services?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<XDocument> ReadSoapAsync(HttpClient client, string operation = "GetServiceDescriptionsEx")
    {
        using var body = new StringContent($"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body><{operation} xmlns="http://www.esri.com/schemas/ArcGIS/10.8" /></soap:Body>
            </soap:Envelope>
            """, Encoding.UTF8, "text/xml");
        using var response = await client.PostAsync("/services", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return XDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static string ChildValue(XElement element, string name)
        => element.Elements().Single(child => child.Name.LocalName == name).Value;
}
