// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Capabilities;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

/// <summary>
/// Integration tests for OGC API Processes endpoints.
/// Tests the adapter layer over the canonical geoprocessing runtime.
/// </summary>
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesEndpointsTests : IClassFixture<WebAppFixture>
{
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";

    private readonly WebAppFixture _fixture;

    public OgcProcessesEndpointsTests(WebAppFixture fixture) => _fixture = fixture;

    // -----------------------------------------------------------------------
    // Landing page
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/processes")]
    public async Task LandingPage_ReturnsValidResponse()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("title").GetString().Should().Contain("Honua");

        var links = json.RootElement.GetProperty("links").EnumerateArray().ToArray();
        links.Should().NotBeEmpty();
        links.Should().Contain(l =>
            l.GetProperty("rel").GetString() == "http://www.opengis.net/def/rel/ogc/1.0/processes");
        links.Should().Contain(l =>
            l.GetProperty("rel").GetString() == "service-desc",
            "OGC API Common Core requires a service-desc link to the API definition");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/processes")]
    public async Task LandingPage_ServiceDescPointsToProcessesOpenApi()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var serviceDescLink = json.RootElement.GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == "service-desc");
        serviceDescLink.GetProperty("href").GetString().Should()
            .Contain("/ogc/processes/openapi.json",
                "service-desc must point to the OGC Processes-specific OpenAPI document");
    }

    // -----------------------------------------------------------------------
    // OpenAPI
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/processes/openapi.json")]
    public async Task OpenApiSpec_ReturnsValidDocument()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/openapi.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("openapi").GetString().Should().StartWith("3.");
        json.RootElement.GetProperty("info").GetProperty("title").GetString().Should()
            .Contain("Processes");
        json.RootElement.GetProperty("paths")
            .GetProperty("/ogc/processes/processes")
            .GetProperty("get")
            .GetProperty("parameters")
            .EnumerateArray()
            .Select(parameter => parameter.GetProperty("name").GetString())
            .Should().Contain(["limit", "offset"]);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/processes/openapi.json")]
    public async Task OpenApiSpec_ProtectedOperationsDeclareApiKeySecurity()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/openapi.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var scheme = json.RootElement.GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("ApiKeyAuth");
        scheme.GetProperty("type").GetString().Should().Be("apiKey");
        scheme.GetProperty("in").GetString().Should().Be("header");
        scheme.GetProperty("name").GetString().Should().Be("X-API-Key");

        json.RootElement.GetProperty("paths")
            .GetProperty("/ogc/processes/processes/{processId}/execution")
            .GetProperty("post")
            .GetProperty("security")
            .GetArrayLength().Should().BeGreaterThan(0);

        json.RootElement.GetProperty("paths")
            .GetProperty("/ogc/processes/jobs")
            .GetProperty("get")
            .GetProperty("security")
            .GetArrayLength().Should().BeGreaterThan(0);
    }

    // -----------------------------------------------------------------------
    // Conformance
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/processes/conformance")]
    public async Task Conformance_ReturnsProcessesCoreClasses()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/conformance");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var conformsTo = json.RootElement.GetProperty("conformsTo").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();

        conformsTo.Should().Contain("http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/core");
        conformsTo.Should().Contain("http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/json");
        conformsTo.Should().Contain("http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/ogc-process-description");
        conformsTo.Should().NotContain("http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/dismiss",
            "V1 exposes DELETE /jobs/{jobId} but does not yet claim full conf/dismiss semantics for finished-job cleanup.");
        conformsTo.Should().NotContain("http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/job-list",
            "V1 job list is MVP-scoped and does not fully implement conf/job-list");
    }

    // -----------------------------------------------------------------------
    // Process list
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /ogc/processes/processes")]
    public async Task ProcessList_ReturnsAtLeastOneProcess()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/processes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var processes = json.RootElement.GetProperty("processes").EnumerateArray().ToArray();
        processes.Should().HaveCount(80, "the canonical plan process plus all 79 catalog Job processes are projected once");

        var first = processes[0];
        first.GetProperty("id").GetString().Should().Be("honua-geoprocessing");
        first.TryGetProperty("jobControlOptions", out var jco).Should().BeTrue();
        jco.EnumerateArray().Select(e => e.GetString()).Should().Contain("async-execute");
        jco.EnumerateArray().Select(e => e.GetString()).Should().NotContain("sync-execute");
        jco.EnumerateArray().Select(e => e.GetString()).Should().NotContain("dismiss");

        var ids = processes.Select(p => p.GetProperty("id").GetString()).ToArray();
        ids.Should().Contain([
            "geometry.buffer",
            "analytics.spatial-join",
            "proximity.near",
            "statistics.summarize",
            "transform.reproject"]);
        ids.Should().NotContain([
            "analytics.cluster",
            "analytics.density",
            "source.geojson",
            "sink.geojson-file",
            "raster.interpolate-kriging"]);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /ogc/processes/processes")]
    public async Task ProcessList_WithLimit_ReturnsAtMostRequestedCount()
    {
        using var response = await _fixture.Client.GetAsync("/ogc/processes/processes?limit=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var processes = json.RootElement.GetProperty("processes").EnumerateArray().ToArray();
        processes.Should().ContainSingle();
        processes[0].GetProperty("id").GetString().Should().Be("honua-geoprocessing");
        var selfLink = json.RootElement.GetProperty("links").EnumerateArray()
            .Single(link => link.GetProperty("rel").GetString() == "self");
        selfLink.GetProperty("href").GetString().Should()
            .EndWith("/ogc/processes/processes?limit=1");

        var nextLink = json.RootElement.GetProperty("links").EnumerateArray()
            .Single(link => link.GetProperty("rel").GetString() == "next");
        nextLink.GetProperty("href").GetString().Should()
            .EndWith("/ogc/processes/processes?limit=1&offset=1");

        using var nextResponse = await _fixture.Client.GetAsync(
            nextLink.GetProperty("href").GetString());
        nextResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var nextJson = JsonDocument.Parse(await nextResponse.Content.ReadAsStringAsync());
        nextJson.RootElement.GetProperty("processes").EnumerateArray().ToArray()
            .Should().ContainSingle()
            .Which.GetProperty("id").GetString().Should().NotBe("honua-geoprocessing");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /ogc/processes/processes")]
    public async Task ProcessList_WithLimit_CanWalkEveryPublishedProcess()
    {
        using var unpagedResponse = await _fixture.Client.GetAsync("/ogc/processes/processes");
        unpagedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var unpagedJson = JsonDocument.Parse(
            await unpagedResponse.Content.ReadAsStringAsync());
        var expectedIds = unpagedJson.RootElement.GetProperty("processes")
            .EnumerateArray()
            .Select(process => process.GetProperty("id").GetString())
            .ToArray();

        var actualIds = new List<string?>();
        string? pageUrl = "/ogc/processes/processes?limit=1";
        while (pageUrl != null)
        {
            using var pageResponse = await _fixture.Client.GetAsync(pageUrl);
            pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var pageJson = JsonDocument.Parse(await pageResponse.Content.ReadAsStringAsync());
            actualIds.AddRange(pageJson.RootElement.GetProperty("processes")
                .EnumerateArray()
                .Select(process => process.GetProperty("id").GetString()));
            actualIds.Count.Should().BeLessThanOrEqualTo(expectedIds.Length);
            pageUrl = pageJson.RootElement.GetProperty("links")
                .EnumerateArray()
                .Where(link => link.GetProperty("rel").GetString() == "next")
                .Select(link => link.GetProperty("href").GetString())
                .SingleOrDefault();
        }

        actualIds.Should().Equal(expectedIds);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /ogc/processes/processes")]
    public async Task ProcessList_WithNonPositiveLimit_Returns400()
    {
        using var zero = await _fixture.Client.GetAsync("/ogc/processes/processes?limit=0");
        using var negative = await _fixture.Client.GetAsync("/ogc/processes/processes?limit=-1");
        using var malformed = await _fixture.Client.GetAsync("/ogc/processes/processes?limit=abc");

        zero.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        negative.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        malformed.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        using var error = JsonDocument.Parse(await malformed.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("title").GetString().Should().Be("Invalid limit");
        error.RootElement.GetProperty("status").GetInt32().Should().Be(400);
        error.RootElement.GetProperty("detail").GetString().Should()
            .Be("The 'limit' parameter must be a positive integer.");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /ogc/processes/processes")]
    public async Task ProcessList_WithInvalidOffset_Returns400()
    {
        using var negative = await _fixture.Client.GetAsync(
            "/ogc/processes/processes?limit=1&offset=-1");
        using var malformed = await _fixture.Client.GetAsync(
            "/ogc/processes/processes?limit=1&offset=abc");

        negative.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var error = JsonDocument.Parse(await malformed.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("title").GetString().Should().Be("Invalid offset");
        error.RootElement.GetProperty("detail").GetString().Should()
            .Be("The 'offset' parameter must be a non-negative integer.");
    }

    // -----------------------------------------------------------------------
    // Process description
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /ogc/processes/processes/{processId}")]
    public async Task ProcessDescription_ValidId_ReturnsDescription()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/processes/honua-geoprocessing");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("id").GetString().Should().Be("honua-geoprocessing");
        json.RootElement.TryGetProperty("inputs", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("outputs", out _).Should().BeTrue();
        json.RootElement.GetProperty("jobControlOptions").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("async-execute");
        json.RootElement.GetProperty("jobControlOptions").EnumerateArray()
            .Select(e => e.GetString()).Should().NotContain("sync-execute");
        json.RootElement.GetProperty("jobControlOptions").EnumerateArray()
            .Select(e => e.GetString()).Should().NotContain("dismiss");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /ogc/processes/processes/{processId}")]
    public async Task ProcessDescription_FirstSliceVectorProcess_ReturnsConcreteInputsAndOutputs()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/processes/geometry.buffer");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("id").GetString().Should().Be("geometry.buffer");
        var wkbSchemas = root.GetProperty("inputs").GetProperty("wkb").GetProperty("schema")
            .GetProperty("oneOf").EnumerateArray().ToArray();
        wkbSchemas.Should().Contain(schema =>
            schema.GetProperty("type").GetString() == "string"
            && schema.GetProperty("contentMediaType").GetString() == "application/wkb"
            && schema.GetProperty("format").GetString() == "byte");
        wkbSchemas.Should().Contain(schema =>
            schema.GetProperty("type").GetString() == "object"
            && schema.GetProperty("contentMediaType").GetString() == "application/geo+json");
        root.GetProperty("inputs").GetProperty("distance").GetProperty("schema")
            .GetProperty("type").GetString().Should().Be("number");
        root.GetProperty("outputs").GetProperty("outputFeatureLayer").GetProperty("schema")
            .GetProperty("contentMediaType").GetString().Should().Be("application/geo+json");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /ogc/processes/processes/{processId}")]
    public async Task ProcessDescription_CatalogJobExamples_AreProjectedDirectly()
    {
        string[] processIds =
        [
            "analytics.spatial-join",
            "proximity.near",
            "statistics.summarize",
            "transform.reproject",
        ];

        foreach (var processId in processIds)
        {
            var response = await _fixture.Client.GetAsync($"/ogc/processes/processes/{processId}");
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"catalog Job process '{processId}' must be OGC-callable");
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /ogc/processes/processes/{processId}")]
    public async Task ProcessDescription_NonJobCatalogEntries_Return404()
    {
        string[] processIds =
        [
            "analytics.cluster",
            "source.geojson",
            "raster.interpolate-kriging",
        ];

        foreach (var processId in processIds)
        {
            var response = await _fixture.Client.GetAsync($"/ogc/processes/processes/{processId}");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"'{processId}' is not classified as a direct Job process");
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /ogc/processes/processes/{processId}")]
    public async Task ProcessDescription_RasterSurfaceProcess_ReturnsDescription()
    {
        // #2698: raster/surface process ids are projected for direct discovery,
        // exactly like the first-slice vector ids. surface.slope is a real registered
        // ProcessDefinition (GdalSurfaceJobExecutor); it previously returned 404.
        var response = await _fixture.Client.GetAsync("/ogc/processes/processes/surface.slope");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("id").GetString().Should().Be("surface.slope");
        root.GetProperty("jobControlOptions").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("async-execute");
        // The GDAL slope tool emits a raster artifact, so the description advertises
        // an outputRaster output.
        root.GetProperty("outputs").GetProperty("outputRaster").GetProperty("schema")
            .GetProperty("contentMediaType").GetString().Should().Be("image/tiff");
        root.GetProperty("outputs").GetProperty("outputRaster").GetProperty("schema")
            .GetProperty("type").GetString().Should().Be("string");
        root.GetProperty("outputs").GetProperty("outputRaster").GetProperty("schema")
            .GetProperty("format").GetString().Should().Be("binary");
        // Execute link is present so callers can drive direct execution.
        root.GetProperty("links").EnumerateArray()
            .Select(l => l.GetProperty("rel").GetString())
            .Should().Contain("http://www.opengis.net/def/rel/ogc/1.0/execute");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /ogc/processes/processes/{processId}")]
    public async Task ProcessDescription_ImageryClassify_AdvertisesDelegatedInferenceDescriptors()
    {
        // #2241: the delegated imagery/ML inference lane is registered with
        // conformant parameter descriptors. The process is advertised even when no
        // inference backend is configured (execution then fails with a clear
        // message — no silent stub), so discovery must always succeed.
        var response = await _fixture.Client.GetAsync("/ogc/processes/processes/imagery.classify");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("id").GetString().Should().Be("imagery.classify");
        root.GetProperty("inputs").TryGetProperty("model", out _).Should().BeTrue(
            "the model reference input must be advertised");
        root.GetProperty("inputs").TryGetProperty("source", out _).Should().BeTrue();
        root.GetProperty("inputs").TryGetProperty("layerId", out _).Should().BeTrue(
            "catalog-raster sourcing must be advertised alongside the inline source");
        root.GetProperty("inputs").TryGetProperty("task", out _).Should().BeTrue();
        // The backend decides whether classification lands as a raster map or as
        // detected features, so both output shapes are advertised.
        root.GetProperty("outputs").TryGetProperty("outputRaster", out _).Should().BeTrue();
        root.GetProperty("outputs").TryGetProperty("outputFeatureLayer", out _).Should().BeTrue();
        root.GetProperty("jobControlOptions").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("async-execute");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /ogc/processes/processes")]
    public async Task ProcessList_IncludesImageryInferenceProcess()
    {
        // #2241: imagery.classify appears in the process list for SDK/agent
        // discovery alongside the vector and raster families.
        var response = await _fixture.Client.GetAsync("/ogc/processes/processes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = json.RootElement.GetProperty("processes").EnumerateArray()
            .Select(p => p.GetProperty("id").GetString())
            .ToArray();
        ids.Should().Contain("imagery.classify");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /ogc/processes/processes")]
    public async Task ProcessList_IncludesRasterSurfaceProcesses()
    {
        // #2698: raster/surface ids appear in the process list alongside the
        // first-slice vector ids so SDK clients can discover them for direct execution.
        var response = await _fixture.Client.GetAsync("/ogc/processes/processes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = json.RootElement.GetProperty("processes").EnumerateArray()
            .Select(p => p.GetProperty("id").GetString())
            .ToArray();
        ids.Should().Contain(["surface.slope", "surface.hillshade", "raster.clip", "raster.zonal-statistics"]);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /ogc/processes/processes")]
    [Endpoint("GET /ogc/processes/processes/{processId}")]
    public async Task CiteEchoFixture_DefaultProfile_IsNotDiscoverable()
    {
        using var listResponse = await _fixture.Client.GetAsync("/ogc/processes/processes");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        list.RootElement.GetProperty("processes").EnumerateArray()
            .Select(process => process.GetProperty("id").GetString())
            .Should().NotContain("honua-cite-echo");

        using var descriptionResponse = await _fixture.Client.GetAsync(
            "/ogc/processes/processes/honua-cite-echo");
        descriptionResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_RasterSurfaceProcess_IsNotRejectedAsUnknownProcess()
    {
        // #2698: direct execution of a raster/surface id must reach the shared async
        // submission pipeline (201 async, or 503 when Redis is unavailable in this
        // test env) rather than 404 no-such-process. A single-step honua-geoprocessing
        // plan wrapping surface.slope has always executed; the direct id now does too.
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/surface.slope/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"source":"AAAA","units":"degrees"}}""",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        response.StatusCode.Should().NotBe(HttpStatusCode.NotImplemented);
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.Created,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.InternalServerError);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_RasterSurfaceProcess_InvalidEnumValue_Returns400()
    {
        // Parity with the vector path: the same shared catalog validation applies on
        // the direct raster path, so a bad enum surfaces as 400 (not 404), proving the
        // process is recognised and validated rather than rejected as unknown.
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/surface.slope/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"source":"AAAA","units":"radians"}}""",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("units");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_NonJobCatalogEntries_Return404()
    {
        foreach (var processId in new[] { "analytics.cluster", "source.geojson", "raster.interpolate-kriging" })
        {
            using var content = new StringContent("{\"inputs\":{}}", Encoding.UTF8, "application/json");
            var response = await _fixture.Client.PostAsync(
                $"/ogc/processes/processes/{processId}/execution",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"'{processId}' is not a directly callable Job process");
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /ogc/processes/processes/{processId}")]
    public async Task ProcessDescription_InvalidId_Returns404()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/processes/nonexistent");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("type").GetString().Should()
            .Contain("no-such-process");
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_ExplicitRespondAsync_ReturnsAsyncAdmission()
    {
        var body = $"{{\"inputs\":{{\"wkb\":\"{PointWkbBase64}\",\"srid\":4326,\"distance\":25.5}}}}";
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/geometry.buffer/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.ServiceUnavailable);
        response.StatusCode.Should().NotBe(HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_WithoutDurableJobStore_ReturnsTypedCapabilityUnavailableRefusal()
    {
        // honua-release#202: Redis is optional for a local install (PostGIS is not). The
        // WebAppFixture runs without Redis, so no durable job store is composed. Submission
        // must be refused up front with a machine-readable receipt an agent can branch on —
        // never a hang, an untyped 500, or a job accepted into a queue that can never drain.
        var body = $"{{\"inputs\":{{\"wkb\":\"{PointWkbBase64}\",\"srid\":4326,\"distance\":25.5}}}}";
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/geometry.buffer/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("type").GetString().Should().Be(CapabilityUnavailableCodes.ProblemType,
            "the refusal must carry a typed problem URI, not about:blank");
        root.GetProperty("status").GetInt32().Should().Be(503);
        root.GetProperty("code").GetString().Should().Be(CapabilityUnavailableCodes.ErrorCode);
        root.GetProperty("missingDependency").GetString().Should().Be(CapabilityUnavailableCodes.RedisDependency);
        root.GetProperty("capability").GetString().Should().Be(CapabilityUnavailableCodes.DurableJobsCapability);
        root.GetProperty("remediation").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("remediationRef").GetString().Should().Be(CapabilityUnavailableCodes.RedisRemediationRef);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_SynchronousWithoutDurableJobStore_RefusesImmediatelyInsteadOfWaiting()
    {
        // The synchronous path waits up to 30s on the terminal-result service. Without a job
        // store the refusal must come from submission, before that wait — the no-hang half of
        // the honua-release#202 contract.
        var body = $"{{\"inputs\":{{\"wkb\":\"{PointWkbBase64}\",\"srid\":4326,\"distance\":25.5}}}}";
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/geometry.buffer/execution");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await _fixture.Client.SendAsync(request);
        stopwatch.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20),
            "submission must refuse before the synchronous 30s terminal wait is entered");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("code").GetString()
            .Should().Be(CapabilityUnavailableCodes.ErrorCode);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}")]
    public async Task JobStatus_WithoutDurableJobStore_ReturnsTypedCapabilityUnavailableRefusal()
    {
        // The whole lifecycle degrades consistently: a client that polls a job id it was never
        // able to obtain gets the same typed receipt as the submission that refused it.
        var response = await _fixture.Client.GetAsync("/ogc/processes/jobs/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("type").GetString()
            .Should().Be(CapabilityUnavailableCodes.ProblemType);
        document.RootElement.GetProperty("missingDependency").GetString()
            .Should().Be(CapabilityUnavailableCodes.RedisDependency);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_UnknownRespondSyncPreference_DefaultsToCanonicalAsyncPath()
    {
        // The canonical plan runner remains async-only because its inner processes can
        // include async-only work. An unknown preference therefore follows its async mode.
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-sync");
        request.Content = new StringContent(
            $"{{\"inputs\":{{\"plan\":{{\"planId\":\"p1\",\"steps\":[{{\"stepId\":\"s1\",\"kind\":\"geoprocess\",\"processId\":\"geometry.buffer\",\"inputs\":{{\"wkb\":\"{PointWkbBase64}\",\"srid\":\"4326\",\"distance\":\"25.5\"}}}}]}}}}}}",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Created, HttpStatusCode.ServiceUnavailable },
            "the executable plan should reach asynchronous admission; body: {0}",
            responseBody);
        response.Headers.Contains("Preference-Applied").Should().BeFalse(
            "respond-sync is not a registered preference and must not be acknowledged");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_AsyncJobCreated_EmitsPreferenceAppliedHeader()
    {
        // A supplied respond-async preference is acknowledged on async admission.
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/geometry.buffer/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            $"{{\"inputs\":{{\"wkb\":\"{PointWkbBase64}\",\"srid\":4326,\"distance\":25.5}}}}",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.Created)
        {
            response.Headers.TryGetValues("Preference-Applied", out var values).Should().BeTrue(
                "the supplied respond-async preference was honored");
            values.Should().Contain("respond-async");
        }
        else
        {
            // 503 when Redis unavailable — skip header assertion
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_FirstSliceVectorProcess_SubmitsConcreteProcessId()
    {
        var body = $"{{\"inputs\":{{\"wkb\":\"{PointWkbBase64}\",\"srid\":4326,\"distance\":25.5}}}}";
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/geometry.buffer/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.ServiceUnavailable);
        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        response.StatusCode.Should().NotBe(HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_FirstSliceProcessWithValueOutputSelection_Submits()
    {
        using var request = CreateFirstSliceProcessWithOutputSelectionRequest("value");

        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public Task Execute_FirstSliceProcessWithReferenceOutputSelection_Returns400()
        => ExecuteFirstSliceProcessWithOutputSelectionReturns400("reference");

    private async Task ExecuteFirstSliceProcessWithOutputSelectionReturns400(
        string transmissionMode)
    {
        using var request = CreateFirstSliceProcessWithOutputSelectionRequest(transmissionMode);

        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("title").GetString()
            .Should().Be("Invalid output selection");
        error.RootElement.GetProperty("detail").GetString()
            .Should().Contain("only supports value transmission");
    }

    private static HttpRequestMessage CreateFirstSliceProcessWithOutputSelectionRequest(
        string transmissionMode)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/ogc/processes/processes/geometry.buffer/execution");
        request.Content = new StringContent(
            $$"""
              {
                "inputs": {
                  "wkb": "{{PointWkbBase64}}",
                  "srid": 4326,
                  "distance": 25.5
                },
                "outputs": {
                  "outputFeatureLayer": {
                    "transmissionMode": "{{transmissionMode}}"
                  }
                }
              }
              """,
            Encoding.UTF8,
            "application/json");
        return request;
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_MissingPlanInput_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("title").GetString().Should().Contain("Invalid");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_PlanMissingPlanId_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"plan":{"steps":[{"stepId":"s1"}]}}}""",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("planId");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_PlanEmptySteps_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"plan":{"planId":"test-plan","steps":[]}}}""",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("step");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_InvalidProcess_Returns404()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/nonexistent/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent("""{"inputs":{"plan":{"steps":[]}}}""", Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_StepMissingKind_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"plan":{"planId":"test-plan","steps":[{"stepId":"s1"}]}}}""",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("kind");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_StepUnsupportedKind_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"plan":{"planId":"test-plan","steps":[{"stepId":"s1","kind":"invalidKind"}]}}}""",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("unsupported step kind");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_UnsupportedArtifactKind_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"plan":{"planId":"test-plan","steps":[{"stepId":"s1","kind":"geoprocess"}],"outputs":["badArtifact"]}}}""",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("artifact kind");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_NonStringStepInputValue_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"plan":{"planId":"p1","steps":[{"stepId":"s1","kind":"geoprocess","inputs":{"distance":100}}]}}}""",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("string value");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_NonStringPlanId_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"plan":{"planId":123,"steps":[{"stepId":"s1","kind":"geoprocess"}]}}}""",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("planId");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_NonObjectStep_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"plan":{"planId":"p1","steps":["bad"]}}}""",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("JSON object");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_OutputsNotArray_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"plan":{"planId":"p1","steps":[{"stepId":"s1","kind":"geoprocess"}],"outputs":{"kind":"scalar"}}}}""",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("array");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_ResponseModeRawWithRespondAsync_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"plan":{"planId":"p1","steps":[{"stepId":"s1","kind":"geoprocess","inputs":{"distance":"100"}}]}},"response":"raw"}""",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("synchronous");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_ResponseModeDocument_IsAccepted()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/geometry.buffer/execution");
        request.Headers.Add("Prefer", "respond-async");
        // Use a real job-callable process so this test isolates response-mode handling.
        request.Content = new StringContent(
            $"{{\"inputs\":{{\"wkb\":\"{PointWkbBase64}\",\"srid\":4326,\"distance\":25.5}},\"response\":\"document\"}}",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);

        // Either 201 (job created) or 503 (no Redis) â€” not 501
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_UnknownProcessId_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"plan":{"planId":"p1","steps":[{"stepId":"s1","kind":"geoprocess","processId":"not.a.process"}]}}}""",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("not.a.process");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_MissingRequiredProcessParameter_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"plan":{"planId":"p1","steps":[{"stepId":"s1","kind":"geoprocess","processId":"geometry.buffer"}]}}}""",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("required parameter");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_DestructivePlanWhenApprovalRequired_Returns403()
    {
        var approvalFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IOperatorApprovalEvaluator>();
                services.AddSingleton<IOperatorApprovalEvaluator>(new DestructiveOnlyApprovalEvaluator());
            });

        try
        {
            await approvalFixture.InitializeAsync();
            var client = approvalFixture.CreateAdminClient();

            using var request = new HttpRequestMessage(HttpMethod.Post,
                "/ogc/processes/processes/honua-geoprocessing/execution");
            request.Headers.Add("Prefer", "respond-async");
            request.Content = new StringContent(
                """{"inputs":{"plan":{"planId":"p1","steps":[{"stepId":"s1","kind":"geoprocess","processId":"import.dataset","inputs":{"connection":"primary","sourcePath":"/staging/parcels.geojson","fileName":"parcels.geojson","tableName":"imported_parcels","layerName":"Parcels"}}]}}}""",
                Encoding.UTF8, "application/json");

            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("title").GetString().Should().Be("Approval required");
            json.RootElement.GetProperty("detail").GetString().Should().Contain("operator.destructive.process");
        }
        finally
        {
            await approvalFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_NonDestructivePlanWithDestructiveOnlyPolicy_IsAccepted()
    {
        var approvalFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IOperatorApprovalEvaluator>();
                services.AddSingleton<IOperatorApprovalEvaluator>(new DestructiveOnlyApprovalEvaluator());
            });

        try
        {
            await approvalFixture.InitializeAsync();
            var client = approvalFixture.CreateAdminClient();

            using var request = new HttpRequestMessage(HttpMethod.Post,
                "/ogc/processes/processes/geometry.buffer/execution");
            request.Headers.Add("Prefer", "respond-async");
            // geometry.buffer is job-callable and non-destructive, so this isolates the
            // destructive-only approval policy without bypassing executable-step validation.
            request.Content = new StringContent(
                $"{{\"inputs\":{{\"wkb\":\"{PointWkbBase64}\",\"srid\":4326,\"distance\":25.5}}}}",
                Encoding.UTF8, "application/json");

            var response = await client.SendAsync(request);

            // Either 201 (job created) or 503 (no Redis) â€” never 403 (approval required),
            // since the plan is non-destructive and the evaluator only gates destructive plans.
            response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            await approvalFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_PlanWithoutGeoprocessStep_Returns400()
    {
        using var content = new StringContent(
            """{"inputs":{"plan":{"planId":"p1","steps":[{"stepId":"s1","kind":"queryFeatures"}]}}}""",
            Encoding.UTF8,
            "application/json");

        var response = await _fixture.Client.PostAsync(
            "/ogc/processes/processes/honua-geoprocessing/execution",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("NO_EXECUTABLE_STEP");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_DuplicateStepIds_Returns400()
    {
        using var content = new StringContent(
            """{"inputs":{"plan":{"planId":"p1","steps":[{"stepId":"dup","kind":"queryFeatures"},{"stepId":"dup","kind":"queryFeatures"}]}}}""",
            Encoding.UTF8,
            "application/json");
        var response = await _fixture.Client.PostAsync(
            "/ogc/processes/processes/honua-geoprocessing/execution",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("Duplicate step identifier");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_UnknownDependsOn_Returns400()
    {
        using var content = new StringContent(
            """{"inputs":{"plan":{"planId":"p1","steps":[{"stepId":"s1","kind":"queryFeatures","dependsOn":["missing-step"]}]}}}""",
            Encoding.UTF8,
            "application/json");
        var response = await _fixture.Client.PostAsync(
            "/ogc/processes/processes/honua-geoprocessing/execution",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("unknown step");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_CyclicDependsOn_Returns400()
    {
        using var content = new StringContent(
            """{"inputs":{"plan":{"planId":"p1","steps":[{"stepId":"s1","kind":"queryFeatures","dependsOn":["s2"]},{"stepId":"s2","kind":"queryFeatures","dependsOn":["s1"]}]}}}""",
            Encoding.UTF8,
            "application/json");
        var response = await _fixture.Client.PostAsync(
            "/ogc/processes/processes/honua-geoprocessing/execution",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("cycle");
    }

    private sealed class DestructiveOnlyApprovalEvaluator : IOperatorApprovalEvaluator
    {
        public ApprovalRequirement Evaluate(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
            => request.IsDestructive
                ? ApprovalRequirement.Required(
                    $"operator.destructive.{request.ResourceType.ToString().ToLowerInvariant()}",
                    "destructive-action-requires-approval")
                : ApprovalRequirement.NotRequired();
    }

    // -----------------------------------------------------------------------
    // Job list
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /ogc/processes/jobs")]
    public async Task JobList_NegativeLimit_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/jobs?limit=-1");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("positive");
    }

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /ogc/processes/jobs")]
    public async Task JobList_ZeroLimit_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/jobs?limit=0");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /ogc/processes/jobs")]
    public async Task JobList_ReturnsJobListObjectOrServiceUnavailable()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/jobs");

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            // No Redis â€” 503 with problem document
            var err = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            err.RootElement.GetProperty("status").GetInt32().Should().Be(503);
            return;
        }

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        json.RootElement.TryGetProperty("jobs", out var jobs).Should().BeTrue();
        jobs.ValueKind.Should().Be(JsonValueKind.Array);
        json.RootElement.TryGetProperty("links", out _).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Job status
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}")]
    public async Task JobStatus_NonexistentJob_ReturnsNotFoundOrServiceUnavailable()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/jobs/nonexistent-job-id");

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            var err = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            err.RootElement.GetProperty("status").GetInt32().Should().Be(503);
            return;
        }

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("type").GetString().Should()
            .Contain("no-such-job");
    }

    // -----------------------------------------------------------------------
    // Job results
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task JobResults_NonexistentJob_ReturnsNotFoundOrServiceUnavailable()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/jobs/nonexistent-job-id/results");
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable);
    }

    // -----------------------------------------------------------------------
    // Dismiss
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task DismissJob_NonexistentJob_ReturnsNotFoundOrServiceUnavailable()
    {
        var response = await _fixture.Client.DeleteAsync("/ogc/processes/jobs/nonexistent-job-id");
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable);
    }

    // -----------------------------------------------------------------------
    // Job list — BH7-009 resource-cap regression
    // -----------------------------------------------------------------------

    // BH7-009 regression: before the fix, ListActiveAsync was called with no limit,
    // so all active jobs were loaded into memory before applying the effectiveLimit
    // break. The fix passes effectiveLimit to ListActiveAsync so the store applies
    // a server-side cap. This test verifies that a positive limit= parameter is
    // accepted and the response shape is valid (or 503 without Redis).
    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /ogc/processes/jobs")]
    public async Task JobList_WithPositiveLimit_AcceptsLimitParameterAndReturnsValidResponse()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/jobs?limit=5");

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            // No Redis in this test environment; 503 is expected.
            var err = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            err.RootElement.GetProperty("status").GetInt32().Should().Be(503);
            return;
        }

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.TryGetProperty("jobs", out var jobs).Should().BeTrue();
        // With limit=5 and no active jobs in the test store, the array is empty.
        jobs.GetArrayLength().Should().BeLessOrEqualTo(5, "the effective limit must be honoured");
    }
}
