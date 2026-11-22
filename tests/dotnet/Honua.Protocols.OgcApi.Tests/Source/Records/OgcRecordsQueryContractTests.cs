// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Records;

[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiRecords)]
public sealed class OgcRecordsQueryContractTests(OgcRecordsEndpointTestsFixture fixture)
    : IClassFixture<OgcRecordsEndpointTestsFixture>
{
    private const string ItemsPath = "/ogc/records/collections/honua-catalog/items";
    private readonly WebAppFixture _fixture = fixture.App;

    [IntegrationTheory]
    [InlineData("../..")]
    [InlineData("..")]
    [InlineData("2026-03-15")]
    [InlineData("2026-03-15T12:00:00")]
    [InlineData("03/15/2026")]
    [InlineData("2026-03-15 12:00:00Z")]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task Items_InvalidDatetime_ReturnsBadRequest(string datetime)
    {
        using var response = await _fixture.Client.GetAsync(ItemsPath + "?datetime=" + Uri.EscapeDataString(datetime));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTheory]
    [InlineData("-181,-90,180,90")]
    [InlineData("-180,-91,180,90")]
    [InlineData("-180,-90,181,90")]
    [InlineData("-180,-90,180,91")]
    [InlineData("-10,-10,10,10,10,0")]
    [InlineData("-10,-10,NaN,10,10,20")]
    [InlineData("-10,-10,0,10,10,Infinity")]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task Items_InvalidBbox_ReturnsBadRequest(string bbox)
    {
        using var response = await _fixture.Client.GetAsync(ItemsPath + "?bbox=" + Uri.EscapeDataString(bbox));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTheory]
    [InlineData("datetime", "2026-03-15T12:00:00Z")]
    [InlineData("datetime", "2026-03-15T12:00:00+02:00")]
    [InlineData("datetime", "../2026-03-15T12:00:00Z")]
    [InlineData("datetime", "2026-03-15T12:00:00Z/..")]
    [InlineData("bbox", "170,-10,-170,10")]
    [InlineData("bbox", "-10,-10,0,10,10,20")]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task Items_ValidQuery_ReturnsOk(string parameter, string value)
    {
        using var response = await _fixture.Client.GetAsync(ItemsPath + "?" + parameter + "=" + Uri.EscapeDataString(value));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTheory]
    [InlineData("")]
    [InlineData("/service%3Atest")]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task Items_UnsupportedAccept_ReturnsNotAcceptable(string suffix)
    {
        using var validResponse = await _fixture.Client.GetAsync(ItemsPath + suffix);
        validResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var request = new HttpRequestMessage(HttpMethod.Get, ItemsPath + suffix);
        request.Headers.Accept.ParseAdd("image/png");
        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }

    [IntegrationTheory]
    [InlineData("application/json, application/geo+json;q=0", "application/json")]
    [InlineData("application/geo+json, application/json;q=0", "application/geo+json")]
    [Operation(Operations.ContentNegotiation)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task Items_Accept_ReturnsNegotiatedContentType(string accept, string contentType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ItemsPath);
        request.Headers.Accept.ParseAdd(accept);
        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be(contentType);
    }
}
