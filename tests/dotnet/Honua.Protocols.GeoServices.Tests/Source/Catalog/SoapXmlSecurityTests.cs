// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

[Protocol(TestProtocols.GeoservicesCatalog)]
[Protocol(TestProtocols.ImageServer)]
public sealed class SoapXmlSecurityTests
{
    [IntegrationTheory]
    [InlineData("/services", "text/xml", "http://schemas.xmlsoap.org/soap/envelope/")]
    [InlineData("/services/alpha/ImageServer", "text/xml", "http://schemas.xmlsoap.org/soap/envelope/")]
    [InlineData("/services", "application/soap+xml", "http://www.w3.org/2003/05/soap-envelope")]
    [InlineData("/services/alpha/ImageServer", "application/soap+xml", "http://www.w3.org/2003/05/soap-envelope")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services")]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task PostSoap_DtdWithExternalEntity_RejectsWithoutResolving(
        string route, string contentType, string soapNamespace)
    {
        // A resolution attempt connects to this listener, even if the reader later
        // rejects the document. No external host or file is needed for the probe.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var factory = ServiceRbacTestFixture.CreateFactory(
            configureServices: services => services.AddSingleton(Substitute.For<IRasterStore>()));
        using var client = factory.CreateClient();
        var request = $"""
            <!DOCTYPE Envelope [<!ENTITY external SYSTEM "http://127.0.0.1:{port}/entity">]>
            <soap:Envelope xmlns:soap="{soapNamespace}">
              <soap:Body>
                <GetServiceDescriptions xmlns="http://www.esri.com/schemas/ArcGIS/10.8">&external;</GetServiceDescriptions>
              </soap:Body>
            </soap:Envelope>
            """;
        using var content = new StringContent(request, Encoding.UTF8, contentType);

        using var response = await client.PostAsync(route, content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Malformed SOAP request.");
        listener.Pending().Should().BeFalse("the XML reader must never resolve an external entity");
    }

    [IntegrationTheory]
    [InlineData("/services")]
    [InlineData("/services/alpha/ImageServer")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services")]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task PostSoap_InvalidEnvelopeWithSchemaLocation_RejectsWithoutLoadingClientSchema(string route)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var factory = ServiceRbacTestFixture.CreateFactory(
            configureServices: services => services.AddSingleton(Substitute.For<IRasterStore>()));
        using var client = factory.CreateClient();
        var request = $"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
                           xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                           xsi:schemaLocation="http://schemas.xmlsoap.org/soap/envelope/ http://127.0.0.1:{port}/schema.xsd">
              <soap:Body><GetServiceDescriptions xmlns="http://www.esri.com/schemas/ArcGIS/10.8" /></soap:Body>
              <soap:Header />
            </soap:Envelope>
            """;
        using var content = new StringContent(request, Encoding.UTF8, "text/xml");

        using var response = await client.PostAsync(route, content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Malformed SOAP request.");
        listener.Pending().Should().BeFalse("a client-supplied schema must not replace the trusted SOAP contract");
    }
}
