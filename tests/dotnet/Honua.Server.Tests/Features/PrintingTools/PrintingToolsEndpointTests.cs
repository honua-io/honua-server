// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Xunit;

namespace Honua.Server.Tests.Features.PrintingTools;

/// <summary>
/// Integration tests for the GeoServices PrintingTools endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.PrintingTools)]
public sealed class PrintingToolsEndpointTests : IAsyncLifetime
{
    private static readonly int[] DefaultOutputSize = [800, 600];
    private static readonly int[] OversizedOutputSize = [10000, 10000];
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    // --- Service Info ---

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("GET /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task")]
    public async Task ServiceInfo_ReturnsTaskMetadata()
    {
        var response = await _client.GetAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("name").GetString().Should().Be("Export Web Map Task");
        root.GetProperty("displayName").GetString().Should().Be("Export Web Map");

        var parameters = root.GetProperty("parameters");
        parameters.GetArrayLength().Should().BeGreaterThanOrEqualTo(3);

        // Verify Layout_Template parameter has choice list (at least MAP_ONLY)
        var layoutParam = parameters.EnumerateArray()
            .FirstOrDefault(p => p.GetProperty("name").GetString() == "Layout_Template");
        layoutParam.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        layoutParam.GetProperty("choiceList").GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        // Verify Format parameter: default must match ResolveFormat fallback (PNG32)
        // regardless of edition so clients that omit Format get consistent behavior.
        var formatParam = parameters.EnumerateArray()
            .FirstOrDefault(p => p.GetProperty("name").GetString() == "Format");
        formatParam.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        formatParam.GetProperty("defaultValue").GetString().Should().Be("PNG32");
        formatParam.GetProperty("choiceList").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
    }

    // --- Synchronous Execute ---

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("POST /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute")]
    public async Task Execute_MapOnly_ReturnsPngImage()
    {
        var webMapJson = CreateMinimalWebMapJson();
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = webMapJson,
            ["Format"] = "PNG32",
            ["Layout_Template"] = "MAP_ONLY"
        });

        var response = await _client.PostAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/execute",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(0);

        // Verify PNG magic bytes
        bytes[0].Should().Be(0x89);
        bytes[1].Should().Be(0x50); // P
        bytes[2].Should().Be(0x4E); // N
        bytes[3].Should().Be(0x47); // G
    }

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("GET /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute")]
    public async Task Execute_Get_MapOnly_ReturnsPngImage()
    {
        var webMapJson = Uri.EscapeDataString(CreateMinimalWebMapJson());

        var response = await _client.GetAsync(
            $"/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/execute?Web_Map_as_JSON={webMapJson}&Format=PNG32&Layout_Template=MAP_ONLY");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("POST /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute")]
    public async Task Execute_PdfFormat_ReturnsPdfDocument()
    {
        var webMapJson = CreateMinimalWebMapJson();
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = webMapJson,
            ["Format"] = "PDF",
            ["Layout_Template"] = "MAP_ONLY"
        });

        var response = await _client.PostAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/execute",
            content);

        // PDF may be blocked by entitlement gating (Community edition in tests).
        // Accept either OK with PDF or Payment Required from license gating.
        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/pdf");
            var bytes = await response.Content.ReadAsByteArrayAsync();
            bytes.Length.Should().BeGreaterThan(0);
            // Verify PDF magic bytes
            bytes[0].Should().Be(0x25); // %
            bytes[1].Should().Be(0x50); // P
            bytes[2].Should().Be(0x44); // D
            bytes[3].Should().Be(0x46); // F
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("POST /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute")]
    public async Task Execute_WithJsonResponseFormat_ReturnsUrlInJson()
    {
        var webMapJson = CreateMinimalWebMapJson();
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = webMapJson,
            ["Format"] = "PNG32",
            ["Layout_Template"] = "MAP_ONLY",
            ["f"] = "json"
        });

        var response = await _client.PostAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/execute",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("results").GetArrayLength().Should().Be(1);
        root.GetProperty("results")[0].GetProperty("paramName").GetString().Should().Be("Output_File");

        // GP contract requires messages array
        root.TryGetProperty("messages", out var messages).Should().BeTrue();
        messages.GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("POST /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute")]
    public async Task Execute_MissingFormat_DefaultsToPng32OnCommunity()
    {
        var webMapJson = CreateMinimalWebMapJson();
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = webMapJson,
            // Format intentionally omitted — should default to PNG32, not PDF
            ["Layout_Template"] = "MAP_ONLY"
        });

        var response = await _client.PostAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/execute",
            content);

        // Community edition: omitted format must not trip the PDF entitlement gate.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("POST /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute")]
    public async Task Execute_MapOnly_MissingOutputSize_ReturnsBadRequest()
    {
        // Web map without exportOptions.outputSize for MAP_ONLY should be rejected
        var webMapJson = JsonSerializer.Serialize(new
        {
            mapOptions = new
            {
                extent = new
                {
                    xmin = -122.5,
                    ymin = 37.5,
                    xmax = -122.0,
                    ymax = 38.0,
                    spatialReference = new { wkid = 4326 }
                }
            },
            operationalLayers = Array.Empty<object>(),
            exportOptions = new { dpi = 96 }
        });

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = webMapJson,
            ["Format"] = "PNG32",
            ["Layout_Template"] = "MAP_ONLY"
        });

        var response = await _client.PostAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/execute",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("POST /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute")]
    public async Task Execute_MapOnly_OversizedOutputSize_ReturnsBadRequest()
    {
        var webMapJson = JsonSerializer.Serialize(new
        {
            mapOptions = new
            {
                extent = new
                {
                    xmin = -122.5,
                    ymin = 37.5,
                    xmax = -122.0,
                    ymax = 38.0,
                    spatialReference = new { wkid = 4326 }
                }
            },
            operationalLayers = Array.Empty<object>(),
            exportOptions = new { dpi = 96, outputSize = OversizedOutputSize }
        });

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = webMapJson,
            ["Format"] = "PNG32",
            ["Layout_Template"] = "MAP_ONLY"
        });

        var response = await _client.PostAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/execute",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("POST /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute")]
    public async Task Execute_MissingExtent_ReturnsBadRequest()
    {
        var webMapJson = JsonSerializer.Serialize(new
        {
            operationalLayers = Array.Empty<object>(),
            exportOptions = new { dpi = 96, outputSize = DefaultOutputSize }
        });

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = webMapJson,
            ["Format"] = "PNG32",
            ["Layout_Template"] = "MAP_ONLY"
        });

        var response = await _client.PostAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/execute",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("POST /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/submitJob")]
    public async Task SubmitJob_MissingExtent_ReturnsBadRequest()
    {
        var webMapJson = JsonSerializer.Serialize(new
        {
            operationalLayers = Array.Empty<object>(),
            exportOptions = new { dpi = 96, outputSize = DefaultOutputSize }
        });

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = webMapJson,
            ["Format"] = "PNG32",
            ["Layout_Template"] = "MAP_ONLY"
        });

        var response = await _client.PostAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/submitJob",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("POST /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute")]
    public async Task Execute_WktOnlySpatialReference_ReturnsBadRequest()
    {
        var webMapJson = JsonSerializer.Serialize(new
        {
            mapOptions = new
            {
                extent = new
                {
                    xmin = -122.5,
                    ymin = 37.5,
                    xmax = -122.0,
                    ymax = 38.0,
                    spatialReference = new { wkt = "GEOGCS[\"GCS_WGS_1984\"]" }
                }
            },
            operationalLayers = Array.Empty<object>(),
            exportOptions = new { dpi = 96, outputSize = DefaultOutputSize }
        });

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = webMapJson,
            ["Format"] = "PNG32",
            ["Layout_Template"] = "MAP_ONLY"
        });

        var response = await _client.PostAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/execute",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("POST /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute")]
    public async Task Execute_MapLevelWktOnlySpatialReference_ReturnsBadRequest()
    {
        // Map-level SR with only WKT (no WKID) should be rejected to prevent
        // silent fallback to EPSG:4326.
        var webMapJson = JsonSerializer.Serialize(new
        {
            mapOptions = new
            {
                extent = new
                {
                    xmin = -122.5,
                    ymin = 37.5,
                    xmax = -122.0,
                    ymax = 38.0
                },
                spatialReference = new { wkt = "PROJCS[\"NAD83_California_zone_5\"]" }
            },
            operationalLayers = Array.Empty<object>(),
            exportOptions = new { dpi = 96, outputSize = DefaultOutputSize }
        });

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = webMapJson,
            ["Format"] = "PNG32",
            ["Layout_Template"] = "MAP_ONLY"
        });

        var response = await _client.PostAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/execute",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("POST /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute")]
    public async Task Execute_InvalidWebMapJson_ReturnsBadRequest()
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = "not-valid-json{{{",
            ["Format"] = "PNG32"
        });

        var response = await _client.PostAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/execute",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("POST /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute")]
    public async Task Execute_UnsupportedFormat_ReturnsBadRequest()
    {
        var webMapJson = CreateMinimalWebMapJson();
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = webMapJson,
            ["Format"] = "TIFF"
        });

        var response = await _client.PostAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/execute",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("POST /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute")]
    public async Task Execute_InvalidTemplate_ReturnsBadRequest()
    {
        var webMapJson = CreateMinimalWebMapJson();
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = webMapJson,
            ["Format"] = "PNG32",
            ["Layout_Template"] = "NonExistentTemplate"
        });

        var response = await _client.PostAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/execute",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- Async Job Submission ---

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("POST /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/submitJob")]
    public async Task SubmitJob_ReturnsJobId()
    {
        var webMapJson = CreateMinimalWebMapJson();
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = webMapJson,
            ["Format"] = "PNG32",
            ["Layout_Template"] = "MAP_ONLY"
        });

        var response = await _client.PostAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/submitJob",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("jobId").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("jobStatus").GetString().Should().Be("esriJobSubmitted");
    }

    // --- GET submitJob ---

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("GET /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/submitJob")]
    public async Task SubmitJob_Get_ReturnsJobId()
    {
        var webMapJson = Uri.EscapeDataString(CreateMinimalWebMapJson());

        var response = await _client.GetAsync(
            $"/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/submitJob?Web_Map_as_JSON={webMapJson}&Format=PNG32&Layout_Template=MAP_ONLY");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("jobId").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("jobStatus").GetString().Should().Be("esriJobSubmitted");
    }

    // --- Get Layout Templates Info Task ---

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("GET /rest/services/Utilities/PrintingTools/GPServer/Get Layout Templates Info Task/execute")]
    public async Task GetLayoutTemplatesInfo_ReturnsTemplateMetadata()
    {
        var response = await _client.GetAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Get%20Layout%20Templates%20Info%20Task/execute");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // GP result shape
        var results = root.GetProperty("results");
        results.GetArrayLength().Should().Be(1);
        results[0].GetProperty("paramName").GetString().Should().Be("Output_JSON");
        results[0].GetProperty("dataType").GetString().Should().Be("GPString");

        // Template array in value (Community edition: only MAP_ONLY visible)
        var templates = results[0].GetProperty("value");
        templates.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        // Verify MAP_ONLY template is present with Esri-canonical fields
        var mapOnly = templates.EnumerateArray()
            .FirstOrDefault(t => t.GetProperty("layoutTemplate").GetString() == "MAP_ONLY");
        mapOnly.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        mapOnly.GetProperty("pageSize").GetArrayLength().Should().Be(2);
        mapOnly.GetProperty("pageUnits").GetString().Should().Be("inches");
        mapOnly.GetProperty("webMapFrameSize").GetArrayLength().Should().Be(2);

        // Verify layoutOptions with element visibility flags
        var layoutOpts = mapOnly.GetProperty("layoutOptions");
        layoutOpts.GetProperty("titleText").GetProperty("isVisible").GetBoolean().Should().BeFalse();
        layoutOpts.GetProperty("legendOptions").GetProperty("isVisible").GetBoolean().Should().BeFalse();

        // Community mode only exposes the ungated MAP_ONLY template.
        var letterPortrait = templates.EnumerateArray()
            .FirstOrDefault(t => t.GetProperty("layoutTemplate").GetString() == "Letter ANSI A Portrait");
        letterPortrait.ValueKind.Should().Be(JsonValueKind.Undefined);

        // GP contract requires messages array
        root.TryGetProperty("messages", out var messages).Should().BeTrue();
        messages.GetArrayLength().Should().Be(0);
    }

    // --- Job Status ---

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("GET /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/jobs/{jobId}")]
    public async Task JobStatus_ValidJobId_ReturnsStatus()
    {
        // First submit a job
        var webMapJson = CreateMinimalWebMapJson();
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = webMapJson,
            ["Format"] = "PNG32",
            ["Layout_Template"] = "MAP_ONLY"
        });

        var submitResponse = await _client.PostAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/submitJob",
            content);
        var submitJson = await submitResponse.Content.ReadAsStringAsync();
        using var submitDoc = JsonDocument.Parse(submitJson);
        var jobId = submitDoc.RootElement.GetProperty("jobId").GetString();

        // Query status
        var statusResponse = await _client.GetAsync(
            $"/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/jobs/{jobId}");

        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var statusJson = await statusResponse.Content.ReadAsStringAsync();
        using var statusDoc = JsonDocument.Parse(statusJson);
        var root = statusDoc.RootElement;

        root.GetProperty("jobId").GetString().Should().Be(jobId);
        root.GetProperty("jobStatus").GetString().Should().NotBeNullOrWhiteSpace();

        // If job already succeeded, verify GP contract includes results with paramUrl
        var status = root.GetProperty("jobStatus").GetString();
        if (status == "esriJobSucceeded")
        {
            root.TryGetProperty("results", out var results).Should().BeTrue();
            results.TryGetProperty("Output_File", out var outputFile).Should().BeTrue();
            outputFile.GetProperty("paramUrl").GetString().Should().Be("results/Output_File");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("GET /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/jobs/{jobId}")]
    public async Task JobStatus_InvalidJobId_ReturnsNotFound()
    {
        var response = await _client.GetAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/jobs/nonexistent-job-id");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- Job Result ---

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("GET /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/jobs/{jobId}/results/Output_File")]
    public async Task JobResult_IncompleteJob_ReturnsBadRequest()
    {
        // Submit a job
        var webMapJson = CreateMinimalWebMapJson();
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = webMapJson,
            ["Format"] = "PNG32",
            ["Layout_Template"] = "MAP_ONLY"
        });

        var submitResponse = await _client.PostAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/submitJob",
            content);
        var submitJson = await submitResponse.Content.ReadAsStringAsync();
        using var submitDoc = JsonDocument.Parse(submitJson);
        var jobId = submitDoc.RootElement.GetProperty("jobId").GetString();

        // Immediately request result (job likely still processing)
        var resultResponse = await _client.GetAsync(
            $"/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/jobs/{jobId}/results/Output_File");

        // Should be BadRequest since job isn't complete yet, or OK if it completed fast
        if (resultResponse.StatusCode == HttpStatusCode.OK)
        {
            // Verify single GP parameter object shape (not wrapped in results[])
            var resultJson = await resultResponse.Content.ReadAsStringAsync();
            using var resultDoc = JsonDocument.Parse(resultJson);
            var resultRoot = resultDoc.RootElement;
            resultRoot.GetProperty("paramName").GetString().Should().Be("Output_File");
            resultRoot.GetProperty("dataType").GetString().Should().Be("GPDataFile");
        }
        else
        {
            resultResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("GET /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/jobs/{jobId}/results/Output_File")]
    public async Task JobResult_NonexistentJob_ReturnsNotFound()
    {
        var response = await _client.GetAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/jobs/does-not-exist/results/Output_File");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- Helper Methods ---

    /// <summary>
    /// Creates a minimal web map JSON for testing with a simple extent.
    /// </summary>
    private static string CreateMinimalWebMapJson()
    {
        return JsonSerializer.Serialize(new
        {
            mapOptions = new
            {
                extent = new
                {
                    xmin = -122.5,
                    ymin = 37.5,
                    xmax = -122.0,
                    ymax = 38.0,
                    spatialReference = new { wkid = 4326 }
                }
            },
            operationalLayers = Array.Empty<object>(),
            exportOptions = new { dpi = 96, outputSize = DefaultOutputSize },
            layoutOptions = new
            {
                titleText = "Test Map",
                copyrightText = "Test Attribution"
            }
        });
    }
}
