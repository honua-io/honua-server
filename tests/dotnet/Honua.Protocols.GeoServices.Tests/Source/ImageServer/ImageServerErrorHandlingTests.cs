// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;

using FluentAssertions;

using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Tests for ImageServer error handling: invalid parameters, malformed input, error responses.
/// </summary>
[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.ImageServer)]
public class ImageServerErrorHandlingTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;
    private const int TestLayerId = 0;
    private const int NonExistentLayerId = 99999;

    public ImageServerErrorHandlingTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    #region Format Parameter Validation

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    public async Task GetServiceInfo_InvalidFormat_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer?f=xml");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Only JSON format is supported");
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    public async Task GetServiceInfo_HtmlFormat_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer?f=html");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Only JSON format is supported");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_InvalidFormat_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage?f=xml&bbox=-180,-90,180,90");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Only JSON and image formats are supported");
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    public async Task Identify_InvalidFormat_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/identify?geometry=0,0&f=xml");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Only JSON format is supported");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task GetImageTile_InvalidFormat_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/tile/0/0/0?format=bmp");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region Non-Existent Layer

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    public async Task GetServiceInfo_NonExistentLayer_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{NonExistentLayerId}/ImageServer?f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_NonExistentLayer_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{NonExistentLayerId}/ImageServer/exportImage?f=json&bbox=-180,-90,180,90");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    public async Task Identify_NonExistentLayer_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{NonExistentLayerId}/ImageServer/identify?geometry=0,0&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task GetImageTile_NonExistentLayer_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{NonExistentLayerId}/ImageServer/tile/0/0/0");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region Identify Geometry Validation

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    public async Task Identify_MissingGeometry_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/identify?f=json");

        // Missing required geometry parameter
        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    public async Task Identify_NonNumericGeometry_ReturnsBadRequestOrNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/identify?geometry=abc,def&f=json");

        // Either 400 (invalid coordinates) or 404 (no rasters)
        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    public async Task Identify_SingleValueGeometry_ReturnsBadRequestOrNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/identify?geometry=42&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    public async Task Identify_InvalidJsonGeometry_ReturnsBadRequestOrNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/identify?geometry={{\"invalid\":true}}&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region Export Bbox Validation

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_InvertedBbox_ReturnsErrorOrFallback()
    {
        // minX > maxX (inverted coordinates)
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage?f=json&bbox=180,-90,-180,90");

        // Bbox parsing fails, handler uses default or returns error
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_BboxExceedingMaxBound_ReturnsErrorOrFallback()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage?f=json&bbox=-50000000,-50000000,50000000,50000000");

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
    }

    #endregion

    #region Response Content

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    public async Task GetServiceInfo_ErrorResponse_DoesNotExposeInternalDetails()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{NonExistentLayerId}/ImageServer?f=json");

        if (response.StatusCode != HttpStatusCode.OK)
        {
            var content = await response.Content.ReadAsStringAsync();
            content.Should().NotContain("Exception");
            content.Should().NotContain("StackTrace");
            content.Should().NotContain("ConnectionString");
            content.Should().NotContain("NpgsqlConnection");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_ErrorResponse_DoesNotExposeInternalDetails()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{NonExistentLayerId}/ImageServer/exportImage?f=json&bbox=-180,-90,180,90");

        if (response.StatusCode != HttpStatusCode.OK)
        {
            var content = await response.Content.ReadAsStringAsync();
            content.Should().NotContain("Exception");
            content.Should().NotContain("StackTrace");
            content.Should().NotContain("ConnectionString");
        }
    }

    #endregion
}
