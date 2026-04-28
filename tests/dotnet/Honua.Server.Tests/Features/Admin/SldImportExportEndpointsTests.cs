// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Styling.Sld;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for the admin SLD import/export endpoints (ticket 375).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class SldImportExportEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/style/import-sld")]
    public async Task ImportSld_PolygonSld10_StoresMapLibreStyleAndReturnsDiagnostics()
    {
        var client = _fixture.CreateAdminClient();
        var sld = LoadFixture("polygon-fill-stroke-sld10.xml");

        using var content = new StringContent(sld, Encoding.UTF8, "application/xml");
        var response = await client.PostAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style/import-sld",
            content);

        response.Be200Ok();
        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(payload, SldStyleJsonContext.Default.ApiResponseSldImportResponse);

        apiResponse.Should().NotBeNull();
        apiResponse!.Success.Should().BeTrue();
        apiResponse.Data!.DetectedVersion.Should().Be("Sld10");
        apiResponse.Data.LayerCount.Should().BeGreaterThan(0);
        apiResponse.Data.MapLibreStyle.HasValue.Should().BeTrue();
        var stored = apiResponse.Data.MapLibreStyle!.Value;
        stored.GetProperty("layers").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/style/import-sld")]
    public async Task ImportSld_Sld11WithSeNamespace_DetectsVersionAndStores()
    {
        var client = _fixture.CreateAdminClient();
        var sld = LoadFixture("sld11-se.xml");

        using var content = new StringContent(sld, Encoding.UTF8, "application/xml");
        var response = await client.PostAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style/import-sld",
            content);

        response.Be200Ok();
        var apiResponse = await DeserializeImportAsync(response);
        apiResponse.Data!.DetectedVersion.Should().Be("Sld11");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/style/import-sld")]
    public async Task ImportSld_MalformedXml_ReturnsBadRequest()
    {
        var client = _fixture.CreateAdminClient();
        var sld = LoadFixture("malformed.xml");

        using var content = new StringContent(sld, Encoding.UTF8, "application/xml");
        var response = await client.PostAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style/import-sld",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/style/import-sld")]
    public async Task ImportSld_XxeAttempt_ReturnsBadRequestWithoutLeak()
    {
        var client = _fixture.CreateAdminClient();
        var sld = LoadFixture("xxe-attempt.xml");

        using var content = new StringContent(sld, Encoding.UTF8, "application/xml");
        var response = await client.PostAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style/import-sld",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("/etc/passwd");
        body.Should().NotContain("ENTITY", "raw entity declarations must not echo into responses");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/style/import-sld")]
    public async Task ImportSld_TextSymbolizer_StoresSymbolLayerWithTextProperties()
    {
        var client = _fixture.CreateAdminClient();
        var sld = LoadFixture("text-sld10.xml");

        using var content = new StringContent(sld, Encoding.UTF8, "application/xml");
        var response = await client.PostAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style/import-sld",
            content);

        response.Be200Ok();
        var apiResponse = await DeserializeImportAsync(response);
        apiResponse.Data!.LayerCount.Should().BeGreaterThan(0);

        var stored = apiResponse.Data.MapLibreStyle!.Value;
        var layers = stored.GetProperty("layers");
        layers.GetArrayLength().Should().BeGreaterThan(0);

        var symbol = layers.EnumerateArray().Single(l => l.GetProperty("type").GetString() == "symbol");
        symbol.GetProperty("layout").GetProperty("text-field").GetString().Should().Be("{name}");
        symbol.GetProperty("paint").GetProperty("text-color").GetString().Should().Be("#000000");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/style/import-sld")]
    public async Task ImportSld_ExternalGraphic_ReturnsWarningDiagnostic()
    {
        var client = _fixture.CreateAdminClient();
        var sld = LoadFixture("external-graphic.xml");

        using var content = new StringContent(sld, Encoding.UTF8, "application/xml");
        var response = await client.PostAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style/import-sld",
            content);

        response.Be200Ok();
        var apiResponse = await DeserializeImportAsync(response);
        apiResponse.Data!.Diagnostics.Should().Contain(d =>
            d.Construct == "ExternalGraphic" && d.Severity == Honua.Server.Features.Infrastructure.Styling.Sld.SldDiagnosticSeverity.Warning);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/style/import-sld")]
    public async Task ImportSld_EmptyBody_ReturnsBadRequest()
    {
        var client = _fixture.CreateAdminClient();

        using var content = new StringContent(string.Empty, Encoding.UTF8, "application/xml");
        var response = await client.PostAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style/import-sld",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/style/import-sld")]
    public async Task ImportSld_NoConvertibleSymbolizers_ReturnsFailureDiagnosticsPayload()
    {
        var client = _fixture.CreateAdminClient();
        const string sld = """
        <?xml version="1.0" encoding="UTF-8"?>
        <StyledLayerDescriptor version="1.0.0" xmlns="http://www.opengis.net/sld">
          <NamedLayer>
            <Name>empty</Name>
            <UserStyle>
              <FeatureTypeStyle>
                <Rule><Name>empty-rule</Name></Rule>
              </FeatureTypeStyle>
            </UserStyle>
          </NamedLayer>
        </StyledLayerDescriptor>
        """;

        using var content = new StringContent(sld, Encoding.UTF8, "application/xml");
        var response = await client.PostAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style/import-sld",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var failure = await DeserializeFailureAsync(response);
        failure.Success.Should().BeFalse();
        failure.Data.Should().NotBeNull();
        failure.Data!.DetectedVersion.Should().Be("Sld10");
        failure.Data.Diagnostics.Should().Contain(d =>
            d.Severity == SldDiagnosticSeverity.Error && d.Construct == "MapLibreLayers");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/style/import-sld")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/style/export-sld")]
    public async Task ExportSld_AfterPolygonImport_ReturnsValidSldXml()
    {
        var client = _fixture.CreateAdminClient();

        var sld = LoadFixture("polygon-fill-stroke-sld10.xml");
        using var importContent = new StringContent(sld, Encoding.UTF8, "application/xml");
        var importResponse = await client.PostAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style/import-sld",
            importContent);
        importResponse.Be200Ok();

        var exportResponse = await client.GetAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style/export-sld");

        exportResponse.Be200Ok();
        exportResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        var xml = await exportResponse.Content.ReadAsStringAsync();
        xml.Should().Contain("StyledLayerDescriptor")
            .And.Contain("PolygonSymbolizer");
        exportResponse.Headers.Should().ContainKey("X-Sld-Diagnostic-Count");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/style/export-sld")]
    public async Task ExportSld_UnconvertibleStoredStyle_ReturnsFailureDiagnosticsPayload()
    {
        var client = _fixture.CreateAdminClient();
        await StoreMapLibreStyleAsync("""
        {
          "version": 8,
          "layers": [
            {
              "id": "background",
              "type": "background",
              "paint": { "background-color": "#ffffff" }
            }
          ]
        }
        """);

        var response = await client.GetAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style/export-sld");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var failure = await DeserializeFailureAsync(response);
        failure.Success.Should().BeFalse();
        failure.Data.Should().NotBeNull();
        failure.Data!.Diagnostics.Should().Contain(d =>
            d.Severity == SldDiagnosticSeverity.Error && d.Construct == "MapLibreLayers");
        failure.Data.Diagnostics.Should().Contain(d => d.Construct == "BackgroundLayer");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/style/export-sld")]
    public async Task ExportSld_StoredStyleMissingLayersArray_ReturnsFailureDiagnosticsPayload()
    {
        var client = _fixture.CreateAdminClient();
        await StoreMapLibreStyleAsync("""
        {
          "version": 8,
          "sources": {}
        }
        """);

        var response = await client.GetAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style/export-sld");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        var failure = await DeserializeFailureAsync(response);
        failure.Success.Should().BeFalse();
        failure.Data!.Diagnostics.Should().Contain(d =>
            d.Severity == SldDiagnosticSeverity.Error && d.Construct == "MapLibreLayers");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/style/import-sld")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/style/export-sld")]
    public async Task RoundTripSld_PreservesPolygonFillSubset()
    {
        var client = _fixture.CreateAdminClient();
        var originalSld = LoadFixture("polygon-fill-stroke-sld10.xml");

        using var importContent = new StringContent(originalSld, Encoding.UTF8, "application/xml");
        var importResponse = await client.PostAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style/import-sld",
            importContent);
        importResponse.Be200Ok();

        var exportResponse = await client.GetAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style/export-sld");
        exportResponse.Be200Ok();
        var roundTripSld = await exportResponse.Content.ReadAsStringAsync();

        using var reImport = new StringContent(roundTripSld, Encoding.UTF8, "application/xml");
        var reImportResponse = await client.PostAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style/import-sld",
            reImport);

        reImportResponse.Be200Ok();
        var reImported = await DeserializeImportAsync(reImportResponse);
        reImported.Data!.LayerCount.Should().BeGreaterThan(0);
    }

    private static async Task<ApiResponse<SldImportResponse>> DeserializeImportAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(payload, SldStyleJsonContext.Default.ApiResponseSldImportResponse)!;
    }

    private static async Task<ApiResponse<SldImportFailureResponse>> DeserializeFailureAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(payload, SldStyleJsonContext.Default.ApiResponseSldImportFailureResponse)!;
    }

    private async Task StoreMapLibreStyleAsync(string styleJson)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE honua.layers
            SET maplibre_style = @style::jsonb,
                geoservices_drawing_info = NULL,
                style_version = COALESCE(style_version, 0) + 1
            WHERE layer_id = @layerId;
            """;
        _ = command.Parameters.AddWithValue("@style", styleJson);
        _ = command.Parameters.AddWithValue("@layerId", WebAppFixture.TestLayerId);

        var rows = await command.ExecuteNonQueryAsync();
        rows.Should().Be(1);
    }

    private static string LoadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "Sld", fileName);
        return File.ReadAllText(path);
    }
}
