// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.EsriProbe;

/// <summary>
/// Live GeoServices compatibility probes. Set HONUA_ESRI_PROBE_BASE_URL to point at the
/// locally running probe server; the default is the dedicated bug-hunt port.
/// </summary>
public sealed class EsriProbeBugHuntTests
{
    private static readonly Uri BaseUri = new(
        Environment.GetEnvironmentVariable("HONUA_ESRI_PROBE_BASE_URL")
        ?? "http://127.0.0.1:18080");

    [IntegrationTest]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_ReverseDatumTransformation_PreservesCoordinates()
    {
        using var client = CreateClient();
        var response = await PostAsync(client,
            "/rest/services/Utilities/Geometry/GeometryServer/project",
            new Dictionary<string, string>
            {
                ["f"] = "json",
                ["geometries"] = "{\"geometryType\":\"esriGeometryPoint\",\"geometries\":[{\"x\":-100,\"y\":40}]}",
                ["inSR"] = "4326",
                ["outSR"] = "4269",
                ["datumTransformation"] = "108001"
            });

        using var document = await ReadJsonAsync(response);
        var point = document.RootElement.GetProperty("geometries")[0];

        // WKID 108001 is the identity NAD83/WGS84 transformation at this precision.
        Assert.Equal(-100, point.GetProperty("x").GetDouble(), 6);
        Assert.Equal(40, point.GetProperty("y").GetDouble(), 6);
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/findTransformations")]
    public async Task FindTransformations_Nad83ToWgs84_ReturnsCandidate()
    {
        using var client = CreateClient();
        var response = await PostAsync(client,
            "/rest/services/Utilities/Geometry/GeometryServer/findTransformations",
            new Dictionary<string, string>
            {
                ["f"] = "json",
                ["inSR"] = "4269",
                ["outSR"] = "4326"
            });

        using var document = await ReadJsonAsync(response);
        Assert.True(document.RootElement.GetProperty("transformations").GetArrayLength() > 0);
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_CircularCurve_PreservesZAndM()
    {
        using var client = CreateClient();
        var geometry = "{\"geometryType\":\"esriGeometryPolyline\",\"geometries\":[{\"hasZ\":true,\"hasM\":true,\"curvePaths\":[[[1,0,3,4],{\"c\":[[0,1,5,6],[0,0]]}]]}]}";
        var response = await PostAsync(client,
            "/rest/services/Utilities/Geometry/GeometryServer/project",
            new Dictionary<string, string>
            {
                ["f"] = "json",
                ["geometries"] = geometry,
                ["inSR"] = "4326",
                ["outSR"] = "3857"
            });

        using var document = await ReadJsonAsync(response);
        var output = document.RootElement.GetProperty("geometries")[0];
        Assert.True(output.GetProperty("hasZ").GetBoolean());
        Assert.True(output.GetProperty("hasM").GetBoolean());
        foreach (var vertex in output.GetProperty("paths")[0].EnumerateArray())
        {
            Assert.Equal(4, vertex.GetArrayLength());
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/densify")]
    public async Task Densify_EllipticArc_AcceptsEsriTrueCurve()
    {
        using var client = CreateClient();
        var geometry = "{\"geometryType\":\"esriGeometryPolyline\",\"geometries\":[{\"curvePaths\":[[[0,0],{\"a\":[[10,0],[5,0],0,0,1.0]}]]}]}";
        var response = await PostAsync(client,
            "/rest/services/Utilities/Geometry/GeometryServer/densify",
            new Dictionary<string, string>
            {
                ["f"] = "json",
                ["geometries"] = geometry,
                ["sr"] = "3857",
                ["maxSegmentLength"] = "1000",
                ["lengthUnit"] = "esriMeters"
            });

        using var document = await ReadJsonAsync(response);
        Assert.False(document.RootElement.TryGetProperty("error", out _));
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/esri_probe_2026/ImageServer/exportImage")]
    public async Task ImageServer_ExportImage_RasterBackedProbeReturnsImage()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(
            "/rest/services/esri_probe_2026/ImageServer/exportImage?f=image&bbox=-123,37,-121,39&bboxSR=4326&imageSR=4326&size=64,64&format=png");

        response.EnsureSuccessStatusCode();
        Assert.True(
            response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true,
            response.Content.Headers.ContentType?.ToString());
    }

    private static HttpClient CreateClient() => new() { BaseAddress = BaseUri };

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string path,
        Dictionary<string, string> values)
    {
        using var content = new FormUrlEncodedContent(values);
        return await client.PostAsync(path, content);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }
}
