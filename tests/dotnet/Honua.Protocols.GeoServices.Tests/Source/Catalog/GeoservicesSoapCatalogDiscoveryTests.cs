// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

[Protocol(TestProtocols.GeoservicesCatalog)]
public sealed class GeoservicesSoapCatalogDiscoveryTests
{
    private static readonly string[] _publishedTypes = ["FeatureServer", "MapServer", "GPServer", "VectorTileServer"];

    private const string ArcGisSoapNamespace = "http://www.esri.com/schemas/ArcGIS/10.8";

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    [Endpoint("POST /services")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}")]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public async Task PostSoapCatalog_AdminAndRoleControlsAgreeWithServiceAndLayerHandoffs()
    {
        var restricted = ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["catalog-reader"]);
        using var factory = CreateFactory(new RbacTestLayerCatalog(
            alphaServiceMetadata: restricted, betaServiceMetadata: restricted,
            alphaLayerMetadata: restricted, betaLayerMetadata: restricted));
        foreach (var role in new[] { "admin", "catalog-reader" })
        {
            using var client = ServiceRbacTestFixture.CreateClient(factory, role);
            await AssertCatalogParityAsync(client, [ServiceRbacTestFixture.AlphaService, ServiceRbacTestFixture.BetaService]);
            foreach (var (service, layer) in new[]
            {
                (ServiceRbacTestFixture.AlphaService, ServiceRbacTestFixture.AlphaLayerId),
                (ServiceRbacTestFixture.BetaService, ServiceRbacTestFixture.BetaLayerId)
            })
            {
                using var taskResponse = await client.GetAsync($"/rest/services/{service}/GPServer/Buffer?f=json");
                var taskBody = await taskResponse.Content.ReadAsStringAsync();
                taskResponse.StatusCode.Should().Be(HttpStatusCode.OK, taskBody);
                using var taskPayload = JsonDocument.Parse(taskBody);
                taskPayload.RootElement.TryGetProperty("error", out _).Should().BeFalse(taskBody);
                taskPayload.RootElement.GetProperty("parameters").ValueKind.Should().Be(JsonValueKind.Array);

                foreach (var protocol in new[] { "FeatureServer", "MapServer" })
                {
                    using var response = await client.GetAsync($"/rest/services/{service}/{protocol}/{layer}?f=json");
                    var body = await response.Content.ReadAsStringAsync();
                    response.StatusCode.Should().Be(HttpStatusCode.OK, body);
                    using var payload = JsonDocument.Parse(body);
                    payload.RootElement.TryGetProperty("error", out _).Should().BeFalse(body);
                    payload.RootElement.GetProperty("id").GetInt32().Should().Be(layer);
                }
            }
        }
        using var wrongRole = ServiceRbacTestFixture.CreateClient(factory, "other-role");
        await AssertDeniedParityAsync(wrongRole, HttpStatusCode.Forbidden);
        await AssertDeniedChildMetadataAsync(wrongRole, HttpStatusCode.Forbidden);
        using var anonymous = factory.CreateClient();
        await AssertDeniedParityAsync(anonymous, HttpStatusCode.Unauthorized);
        await AssertDeniedChildMetadataAsync(anonymous, HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// honua-server#4783: the catalog handoff must hold past metadata. A caller the catalog
    /// lists a role-restricted service to (admin through its built-in wildcard grant, or the
    /// role the policy names) must not be refused by the FeatureServer and MapServer
    /// operations a client runs next, while a wrong role and anonymous stay refused.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/query")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/queryDomains")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/legend")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/layers")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/queryDomains")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/query")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public async Task RestCatalog_AdminAndRoleOperationHandoffsAgreeWithCatalogVisibility()
    {
        var restricted = ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["catalog-reader"]);
        var catalog = new RbacTestLayerCatalog(
            alphaServiceMetadata: restricted, betaServiceMetadata: restricted,
            alphaLayerMetadata: restricted, betaLayerMetadata: restricted);
        using var factory = ServiceRbacTestFixture.CreateFactory(
            () => catalog,
            services =>
            {
                services.AddSingleton(Substitute.For<IRasterStore>());
                services.RemoveAll<ICrsDetectionService>();
                services.AddSingleton<ICrsDetectionService, NoopCrsDetectionService>();
                services.RemoveAll<ILayerStyleCatalog>();
                services.AddSingleton(Substitute.For<ILayerStyleCatalog>());
            });

        foreach (var role in new[] { "admin", "catalog-reader" })
        {
            using var client = ServiceRbacTestFixture.CreateClient(factory, role);
            await AssertCatalogParityAsync(client, [ServiceRbacTestFixture.AlphaService, ServiceRbacTestFixture.BetaService]);
            var outcomes = await ReadOperationHandoffOutcomesAsync(client);
            outcomes.Where(outcome => outcome.Value != 0)
                .Should().BeEmpty($"the catalog lists both services to '{role}'");
        }

        using var wrongRole = ServiceRbacTestFixture.CreateClient(factory, "other-role");
        await AssertDeniedParityAsync(wrongRole, HttpStatusCode.Forbidden);
        (await ReadOperationHandoffOutcomesAsync(wrongRole)).Should().OnlyContain(outcome => outcome.Value == 403);

        using var anonymous = factory.CreateClient();
        await AssertDeniedParityAsync(anonymous, HttpStatusCode.Unauthorized);
        (await ReadOperationHandoffOutcomesAsync(anonymous)).Should().OnlyContain(outcome => outcome.Value == 499);
    }

    private static IEnumerable<string> OperationHandoffPaths()
    {
        foreach (var (service, layer) in new[]
        {
            (ServiceRbacTestFixture.AlphaService, ServiceRbacTestFixture.AlphaLayerId),
            (ServiceRbacTestFixture.BetaService, ServiceRbacTestFixture.BetaLayerId)
        })
        {
            var root = $"/rest/services/{service}";
            yield return $"{root}/FeatureServer/query?layerDefs=%7B%22{layer}%22%3A%221%3D1%22%7D&returnGeometry=false&f=json";
            yield return $"{root}/FeatureServer/{layer}/query?where=1%3D1&returnGeometry=false&f=json";
            yield return $"{root}/FeatureServer/queryDomains?layers={layer}&f=json";
            yield return $"{root}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json";
            yield return $"{root}/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&layers=all&tolerance=2&f=json";
            yield return $"{root}/MapServer/find?searchText=test&layers={layer}&f=json";
            yield return $"{root}/MapServer/legend?f=json";
            yield return $"{root}/MapServer/layers?f=json";
            yield return $"{root}/MapServer/queryDomains?layers={layer}&f=json";
            yield return $"{root}/MapServer/{layer}/query?where=1%3D1&returnGeometry=false&f=json";
            yield return $"{root}/GPServer/Buffer?f=json";
        }
    }

    /// <summary>
    /// Returns the outcome of every operation handoff keyed by path: 0 for a successful JSON
    /// response, otherwise the Esri error code (403 forbidden, 499 token required) or the HTTP
    /// status. A refusal must not carry layer content.
    /// </summary>
    private static async Task<Dictionary<string, int>> ReadOperationHandoffOutcomesAsync(HttpClient client)
    {
        var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in OperationHandoffPaths())
        {
            using var response = await client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            var outcome = response.StatusCode == HttpStatusCode.OK ? 0 : (int)response.StatusCode;
            if (outcome == 0)
            {
                using var payload = JsonDocument.Parse(body);
                if (payload.RootElement.TryGetProperty("error", out var error))
                {
                    outcome = error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number
                        ? code.GetInt32()
                        : -1;
                }
            }

            if (outcome is 403 or 499)
            {
                body.Should().NotContain("Alpha Layer").And.NotContain("Beta Layer", path);
            }

            outcomes[path] = outcome;
        }

        return outcomes;
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.GeoservicesCatalog, "GetServiceDescriptions")]
    [Endpoint("GET /rest/services")]
    [Endpoint("POST /services")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer")]
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
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer")]
    public async Task PostSoapCatalog_DeniedDirectory_UsesSoapFaultWithRestStatus()
    {
        var protectedPolicy = ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["catalog-reader"]);
        using var factory = CreateFactory(new RbacTestLayerCatalog(
            alphaServiceMetadata: protectedPolicy,
            betaServiceMetadata: protectedPolicy,
            alphaLayerMetadata: protectedPolicy,
            betaLayerMetadata: protectedPolicy));

        var provider = factory.Services.GetRequiredService<IMetadataV2GraphProvider>();
        var before = await provider.GetCurrentAsync();
        using var reader = ServiceRbacTestFixture.CreateClient(factory, "catalog-reader");
        await AssertCatalogParityAsync(reader, [ServiceRbacTestFixture.AlphaService, ServiceRbacTestFixture.BetaService]);

        using var anonymous = factory.CreateClient();
        await AssertDeniedParityAsync(anonymous, HttpStatusCode.Unauthorized);

        using var wrongRole = ServiceRbacTestFixture.CreateClient(factory, "other-role");
        await AssertDeniedParityAsync(wrongRole, HttpStatusCode.Forbidden);

        (await provider.GetCurrentAsync()).Should().BeEquivalentTo(before,
            "denied catalog and metadata requests must leave the seeded graph unchanged");
        await AssertCatalogParityAsync(reader, [ServiceRbacTestFixture.AlphaService, ServiceRbacTestFixture.BetaService]);
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
        foreach (var service in soapDescriptions.Where(service => service.Type == "GPServer"))
        {
            new Uri(service.SoapUrl).AbsolutePath.Should().Be($"/services/{service.Name}/GPServer");
            new Uri(service.RestUrl).AbsolutePath.Should().Be($"/rest/services/{service.Name}/GPServer");
        }
        var soapEntries = soapDescriptions
            .Select(entry => new CatalogEntry(entry.Name, entry.Type, entry.RestUrl))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Type, StringComparer.Ordinal)
            .ToArray();

        soapEntries.Should().Equal(restEntries);
        soapEntries.Select(entry => entry.Name).Distinct(StringComparer.Ordinal)
            .Should().BeEquivalentTo(expectedNames);
        soapEntries.Select(entry => (entry.Name, entry.Type)).Should().BeEquivalentTo(
            expectedNames.SelectMany(name => _publishedTypes
                .Select(type => (name, type))),
            "both catalogs must enumerate every published non-raster service in this fixture");

        foreach (var entry in soapEntries)
        {
            using var handoff = await client.GetAsync(new Uri(entry.Url).PathAndQuery);
            var body = await handoff.Content.ReadAsStringAsync();
            handoff.StatusCode.Should().Be(HttpStatusCode.OK, body);
            using var payload = JsonDocument.Parse(body);
            var root = payload.RootElement;
            root.TryGetProperty("error", out _).Should().BeFalse(body);

            var nameProperty = entry.Type switch
            {
                "FeatureServer" => "serviceName",
                "MapServer" => "mapName",
                "VectorTileServer" => "name",
                "GPServer" => "serviceDescription",
                _ => throw new InvalidOperationException($"No metadata result assertion for {entry.Type}")
            };
            ServiceRbacTestFixture.GetPropertyCaseInsensitive(root, nameProperty).GetString()
                .Should().Be(entry.Type == "GPServer" ? $"Geoprocessing service for {entry.Name}" : entry.Name);

            if (entry.Type is "FeatureServer" or "MapServer")
            {
                var layer = ServiceRbacTestFixture.GetPropertyCaseInsensitive(root, "layers")
                    .EnumerateArray().Should().ContainSingle().Subject;
                var isAlpha = entry.Name == ServiceRbacTestFixture.AlphaService;
                ServiceRbacTestFixture.GetPropertyCaseInsensitive(layer, "id").GetInt32()
                    .Should().Be(isAlpha ? ServiceRbacTestFixture.AlphaLayerId : ServiceRbacTestFixture.BetaLayerId);
                ServiceRbacTestFixture.GetPropertyCaseInsensitive(layer, "name").GetString()
                    .Should().Be(isAlpha ? "Alpha Layer" : "Beta Layer");
            }

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
        restPayload.RootElement.EnumerateObject().Select(property => property.Name).Should().Equal("error");

        using var soapResponse = await PostSoapAsync(client);
        var soapBody = await soapResponse.Content.ReadAsStringAsync();
        soapResponse.StatusCode.Should().Be(expectedStatus, soapBody);
        soapResponse.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
        XDocument.Parse(soapBody).Descendants()
            .Should().ContainSingle(element => element.Name.LocalName == "Fault");
        ReadSoapEntries(XDocument.Parse(soapBody)).Should().BeEmpty();

        using var folderResponse = await PostSoapAsync(
            client,
            $"<GetServiceDescriptionsEx xmlns=\"{ArcGisSoapNamespace}\"><folderName>private</folderName></GetServiceDescriptionsEx>");
        var folderBody = await folderResponse.Content.ReadAsStringAsync();
        folderResponse.StatusCode.Should().Be(expectedStatus, folderBody);
        XDocument.Parse(folderBody).Descendants()
            .Should().ContainSingle(element => element.Name.LocalName == "Fault");
        ReadSoapEntries(XDocument.Parse(folderBody)).Should().BeEmpty();

        foreach (var body in new[] { restBody, soapBody, folderBody })
        {
            body.Should().NotContain("Alpha Layer").And.NotContain("Beta Layer")
                .And.NotContain("/rest/services/alpha/").And.NotContain("/rest/services/beta/");
        }

        foreach (var service in new[] { ServiceRbacTestFixture.AlphaService, ServiceRbacTestFixture.BetaService })
        {
            foreach (var protocol in _publishedTypes)
            {
                using var handoff = await client.GetAsync($"/rest/services/{service}/{protocol}?f=json");
                var body = await handoff.Content.ReadAsStringAsync();
                handoff.StatusCode.Should().Be(HttpStatusCode.OK, body);
                using var payload = JsonDocument.Parse(body);
                payload.RootElement.EnumerateObject().Select(property => property.Name).Should().Equal("error");
                ServiceRbacTestFixture.GetPropertyCaseInsensitive(
                        ServiceRbacTestFixture.GetPropertyCaseInsensitive(payload.RootElement, "error"), "code")
                    .GetInt32().Should().Be(expectedStatus == HttpStatusCode.Unauthorized ? 499 : 403);
                body.Should().NotContain("Alpha Layer").And.NotContain("Beta Layer");
            }
        }
    }

    private static async Task AssertDeniedChildMetadataAsync(HttpClient client, HttpStatusCode expectedStatus)
    {
        foreach (var (service, layer) in new[]
        {
            (ServiceRbacTestFixture.AlphaService, ServiceRbacTestFixture.AlphaLayerId),
            (ServiceRbacTestFixture.BetaService, ServiceRbacTestFixture.BetaLayerId)
        })
        {
            foreach (var child in new[] { $"FeatureServer/{layer}", $"MapServer/{layer}", "GPServer/Buffer" })
            {
                using var response = await client.GetAsync($"/rest/services/{service}/{child}?f=json");
                var body = await response.Content.ReadAsStringAsync();
                response.StatusCode.Should().Be(HttpStatusCode.OK, body);
                using var payload = JsonDocument.Parse(body);
                payload.RootElement.EnumerateObject().Select(property => property.Name).Should().Equal("error");
                payload.RootElement.GetProperty("error").GetProperty("code").GetInt32()
                    .Should().Be(expectedStatus == HttpStatusCode.Unauthorized ? 499 : 403);
                body.Should().NotContain("Alpha Layer").And.NotContain("Beta Layer").And.NotContain("parameters");
            }
        }
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
