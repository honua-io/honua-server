// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

[Protocol(TestProtocols.GeoservicesCatalog)]
public sealed class GeoservicesSoapCatalogDiscoveryTests
{
    private const string ArcGisSoapNamespace = "http://www.esri.com/schemas/ArcGIS/10.8";

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.GeoservicesCatalog, "GetServiceDescriptions")]
    [Endpoint("GET /rest/services")]
    [Endpoint("POST /services")]
    public async Task PostSoapCatalog_UsesSameRbacFilteredEntriesAsRestDirectory()
    {
        var publicPolicy = ServiceRbacTestFixture.CreateServiceMetadata(allowAnonymous: true);
        var protectedPolicy = ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["catalog-reader"]);
        using var factory = CreateFactory(new RbacTestLayerCatalog(
            alphaServiceMetadata: publicPolicy,
            betaServiceMetadata: protectedPolicy,
            alphaLayerMetadata: publicPolicy,
            betaLayerMetadata: protectedPolicy));

        using var anonymous = factory.CreateClient();
        await AssertCatalogParityAsync(anonymous, expectedNames: [ServiceRbacTestFixture.AlphaService]);

        using var authenticated = ServiceRbacTestFixture.CreateClient(factory, "catalog-reader");
        await AssertCatalogParityAsync(
            authenticated,
            expectedNames: [ServiceRbacTestFixture.AlphaService, ServiceRbacTestFixture.BetaService]);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    [Endpoint("POST /services")]
    public async Task PostSoapCatalog_DeniedDirectory_UsesSoapFaultWithRestStatus()
    {
        var protectedPolicy = ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["catalog-reader"]);
        using var factory = CreateFactory(new RbacTestLayerCatalog(
            alphaServiceMetadata: protectedPolicy,
            betaServiceMetadata: protectedPolicy,
            alphaLayerMetadata: protectedPolicy,
            betaLayerMetadata: protectedPolicy));

        using var anonymous = factory.CreateClient();
        await AssertDeniedParityAsync(anonymous, HttpStatusCode.Unauthorized);

        using var wrongRole = ServiceRbacTestFixture.CreateClient(factory, "other-role");
        await AssertDeniedParityAsync(wrongRole, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /services")]
    [Endpoint("POST /services")]
    public async Task SoapCatalog_PublicBaseUrlProjectsWsdlSoapAndRestUrls()
    {
        const string publicBaseUrl = "https://catalog.public.example.test/gis";
        var publicPolicy = ServiceRbacTestFixture.CreateServiceMetadata(allowAnonymous: true);
        using var factory = CreateFactory(new RbacTestLayerCatalog(
                alphaServiceMetadata: publicPolicy,
                betaServiceMetadata: publicPolicy,
                alphaLayerMetadata: publicPolicy,
                betaLayerMetadata: publicPolicy))
            .WithWebHostBuilder(builder => builder.UseSetting("Public:BaseUrl", publicBaseUrl));
        using var client = factory.CreateClient();

        using var wsdlResponse = await client.GetAsync("/services?wsdl");
        wsdlResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        wsdlResponse.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
        var wsdl = XDocument.Parse(await wsdlResponse.Content.ReadAsStringAsync());
        var wsdlAddresses = wsdl.Descendants()
            .Where(element => element.Name.LocalName == "address")
            .Select(element => element.Attribute("location")?.Value)
            .ToArray();
        wsdlAddresses.Should().OnlyContain(address => address == $"{publicBaseUrl}/services");
        var wsdlElementNames = wsdl.Descendants()
            .Where(element => element.Name.LocalName == "element")
            .Select(element => element.Attribute("name")?.Value)
            .ToArray();
        wsdlElementNames.Should().Contain("RestUrl");

        using var soapResponse = await PostSoapAsync(client);
        soapResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var soap = XDocument.Parse(await soapResponse.Content.ReadAsStringAsync());
        var descriptions = ReadSoapEntries(soap);
        descriptions.Should().NotBeEmpty();
        descriptions.Should().OnlyContain(entry => entry.RestUrl.StartsWith(
            $"{publicBaseUrl}/rest/services/",
            StringComparison.Ordinal));
        descriptions.Should().OnlyContain(entry => entry.SoapUrl.StartsWith(publicBaseUrl, StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /services")]
    public async Task GetSoapCatalog_WithoutWsdlFlag_ReturnsNotFound()
    {
        using var factory = CreateFactory(new RbacTestLayerCatalog());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/services");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task AssertCatalogParityAsync(HttpClient client, string[] expectedNames)
    {
        using var restResponse = await client.GetAsync("/rest/services?f=json");
        restResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var restBody = await restResponse.Content.ReadAsStringAsync();
        using var restPayload = JsonDocument.Parse(restBody);
        var restEntries = ServiceRbacTestFixture
            .GetPropertyCaseInsensitive(restPayload.RootElement, "services")
            .EnumerateArray()
            .Select(service => new CatalogEntry(
                ServiceRbacTestFixture.GetPropertyCaseInsensitive(service, "name").GetString()!,
                ServiceRbacTestFixture.GetPropertyCaseInsensitive(service, "type").GetString()!,
                ServiceRbacTestFixture.GetPropertyCaseInsensitive(service, "url").GetString()!))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Type, StringComparer.Ordinal)
            .ToArray();

        using var soapResponse = await PostSoapAsync(client);
        soapResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var soapDescriptions = ReadSoapEntries(XDocument.Parse(await soapResponse.Content.ReadAsStringAsync()));
        var soapEntries = soapDescriptions
            .Select(entry => new CatalogEntry(entry.Name, entry.Type, entry.RestUrl))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Type, StringComparer.Ordinal)
            .ToArray();

        soapEntries.Should().Equal(restEntries);
        soapEntries.Select(entry => entry.Name).Distinct(StringComparer.Ordinal)
            .Should().BeEquivalentTo(expectedNames);

        foreach (var entry in soapEntries)
        {
            using var handoff = await client.GetAsync(new Uri(entry.Url).PathAndQuery);
            handoff.StatusCode.Should().NotBe(HttpStatusCode.NotFound);

            if (entry.Type == "FeatureServer")
            {
                using var featureServerPayload = JsonDocument.Parse(await handoff.Content.ReadAsStringAsync());
                var canonicalCapabilities = ServiceRbacTestFixture
                    .GetPropertyCaseInsensitive(featureServerPayload.RootElement, "capabilities")
                    .GetString();
                soapDescriptions.Single(description =>
                        description.Name == entry.Name && description.Type == entry.Type)
                    .Capabilities.Should().Be(canonicalCapabilities);
            }
        }
    }

    private static async Task AssertDeniedParityAsync(HttpClient client, HttpStatusCode expectedStatus)
    {
        using var restResponse = await client.GetAsync("/rest/services?f=json");
        var restBody = await restResponse.Content.ReadAsStringAsync();
        restResponse.StatusCode.Should().Be(HttpStatusCode.OK, restBody);
        using var restPayload = JsonDocument.Parse(restBody);
        var restError = ServiceRbacTestFixture.GetPropertyCaseInsensitive(restPayload.RootElement, "error");
        var restErrorCode = ServiceRbacTestFixture.GetPropertyCaseInsensitive(restError, "code").GetInt32();
        restErrorCode.Should().Be(expectedStatus == HttpStatusCode.Unauthorized ? 499 : 403);

        using var soapResponse = await PostSoapAsync(client);
        var soapBody = await soapResponse.Content.ReadAsStringAsync();
        soapResponse.StatusCode.Should().Be(expectedStatus, soapBody);
        soapResponse.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
        XDocument.Parse(soapBody).Descendants()
            .Should().ContainSingle(element => element.Name.LocalName == "Fault");

        using var folderResponse = await PostSoapAsync(
            client,
            $"<GetServiceDescriptionsEx xmlns=\"{ArcGisSoapNamespace}\"><folderName>private</folderName></GetServiceDescriptionsEx>");
        var folderBody = await folderResponse.Content.ReadAsStringAsync();
        folderResponse.StatusCode.Should().Be(expectedStatus, folderBody);
        XDocument.Parse(folderBody).Descendants()
            .Should().ContainSingle(element => element.Name.LocalName == "Fault");
    }

    private static async Task<HttpResponseMessage> PostSoapAsync(
        HttpClient client,
        string? operation = null)
    {
        operation ??= $"<GetServiceDescriptions xmlns=\"{ArcGisSoapNamespace}\" />";
        var request = $"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                {operation}
              </soap:Body>
            </soap:Envelope>
            """;
        using var content = new StringContent(request, Encoding.UTF8, "text/xml");
        return await client.PostAsync("/services", content);
    }

    private static SoapCatalogEntry[] ReadSoapEntries(XDocument document)
        => document.Descendants()
            .Where(element => element.Name.LocalName == "ServiceDescription")
            .Select(description => new SoapCatalogEntry(
                ChildValue(description, "Name"),
                ChildValue(description, "Type"),
                ChildValue(description, "Url"),
                ChildValue(description, "RestUrl"),
                ChildValue(description, "Capabilities")))
            .ToArray();

    private static string ChildValue(XElement parent, string localName)
        => parent.Elements().Single(element => element.Name.LocalName == localName).Value;

    private static WebApplicationFactory<Program> CreateFactory(RbacTestLayerCatalog catalog)
        => ServiceRbacTestFixture.CreateFactory(
            () => catalog,
            services => services.AddSingleton(Substitute.For<IRasterStore>()));

    private sealed record CatalogEntry(string Name, string Type, string Url);

    private sealed record SoapCatalogEntry(
        string Name,
        string Type,
        string SoapUrl,
        string RestUrl,
        string Capabilities);
}
