// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Real import and discovery results paired with anonymous denial on the same host.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class ImportReadWriteContractTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(client =>
            client.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();


    [IntegrationTest]
    [Operation(Operations.TableDiscovery)]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task Admin_GetTables_ShouldReturnTableList()
    {

        using var denied = await _fixture.Client.GetAsync("/api/v1/admin/connections/test/tables");
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await denied.Content.ReadAsStringAsync()).Should().NotContain("geometryColumn").And.NotContain("\"tables\"");

        var response = await _client.GetAsync("/api/v1/admin/connections/test/tables");

        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var json = JsonDocument.Parse(payload);
        var tables = json.RootElement.GetProperty("tables");
        tables.ValueKind.Should().Be(JsonValueKind.Array);
        tables.GetArrayLength().Should().BeGreaterThan(
            0,
            "discovery against the seeded fixture database must return at least one spatial table");

        foreach (var table in tables.EnumerateArray())
        {
            table.GetProperty("schema").GetString().Should().NotBeNullOrWhiteSpace();
            table.GetProperty("table").GetString().Should().NotBeNullOrWhiteSpace();
        }

        var hasGeometryColumn = false;
        foreach (var table in tables.EnumerateArray())
        {
            if (table.TryGetProperty("geometryColumn", out var column) &&
                column.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(column.GetString()))
            {
                hasGeometryColumn = true;
                break;
            }
        }

        hasGeometryColumn.Should().BeTrue(
            "PostGIS discovery must report the geometry column of at least one seeded spatial table");

        var unknown = await _client.GetAsync("/api/v1/admin/connections/no-such-connection-4390/tables");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_UploadFile_ShouldAcceptValidFormats()
    {

        var tableName = $"coverage_import_{Guid.NewGuid():N}"[..30];
        var geoJsonContent = """
            {
                "type": "FeatureCollection",
                "features": [
                    {
                        "type": "Feature",
                        "geometry": {
                            "type": "Point",
                            "coordinates": [-122.0, 37.0]
                        },
                        "properties": {
                            "name": "Test Point"
                        }
                    }
                ]
            }
            """;

        using var multipartContent = new MultipartFormDataContent();
        multipartContent.Add(new StringContent(geoJsonContent), "file", "test.geojson");
        multipartContent.Add(new StringContent(tableName), "TableName");
        multipartContent.Add(new StringContent("4326"), "TargetSrid");

        using var denied = await _fixture.Client.PostAsync("/api/v1/admin/import/upload", multipartContent);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await denied.Content.ReadAsStringAsync()).Should().NotContain("physicalTableName").And.NotContain("featureCount");
        using var before = await _client.GetAsync("/api/v1/admin/connections/test/tables");
        before.StatusCode.Should().Be(HttpStatusCode.OK);
        (await before.Content.ReadAsStringAsync()).Should().NotContain(tableName,
            "a refused upload must not create its requested table");

        var response = await _client.PostAsync("/api/v1/admin/import/upload", multipartContent);

        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);

        using var result = JsonDocument.Parse(payload);
        result.RootElement.GetProperty("success").GetBoolean().Should().BeTrue(payload);
        result.RootElement.GetProperty("tableName").GetString().Should().Be(tableName);
        result.RootElement.GetProperty("format").GetString().Should().Be("GeoJson");
        result.RootElement.GetProperty("featureCount").GetInt32().Should().Be(
            1,
            "the posted FeatureCollection carries exactly one feature, so an import that silently dropped it must fail");
        result.RootElement.GetProperty("detectedSrid").GetInt32().Should().Be(4326);

        var physicalTableName = result.RootElement.GetProperty("physicalTableName").GetString();
        physicalTableName.Should().NotBeNullOrWhiteSpace();

        var discovery = await _client.GetAsync("/api/v1/admin/connections/test/tables");
        var discoveryPayload = await discovery.Content.ReadAsStringAsync();
        discovery.StatusCode.Should().Be(HttpStatusCode.OK, discoveryPayload);
        discoveryPayload.Should().Contain(
            physicalTableName!,
            "an upload that reported success must have created a table the server can discover");
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    [Endpoint("GET /api/v1/admin/import/jobs/{jobId}")]
    public async Task Import_GetJobStatus_ShouldReturnJobStatus()
    {

        var unknownJobId = Guid.NewGuid().ToString();

        var unknown = await _client.GetAsync($"/api/v1/admin/import/jobs/{unknownJobId}");

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var unknownBody = await unknown.Content.ReadAsStringAsync();
        unknownBody.Should().NotContain("\"currentPhase\"");
        unknownBody.Should().NotContain("\"tableName\"");

        var tableName = $"coverage_jobstatus_{Guid.NewGuid():N}"[..30];
        var geoJsonContent = """
            {
                "type": "FeatureCollection",
                "features": [
                    {
                        "type": "Feature",
                        "geometry": { "type": "Point", "coordinates": [-122.0, 37.0] },
                        "properties": { "name": "Job Status Point" }
                    }
                ]
            }
            """;

        using var multipartContent = new MultipartFormDataContent();
        var fileContent = new StringContent(geoJsonContent, Encoding.UTF8, "application/json");
        fileContent.Headers.ContentDisposition =
            new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
            {
                Name = "File",
                FileName = "job-status.geojson",
            };
        multipartContent.Add(fileContent);
        multipartContent.Add(new StringContent(tableName), "TableName");
        multipartContent.Add(new StringContent("true"), "ForceBackground");

        var queued = await _client.PostAsync("/api/v1/admin/import/upload", multipartContent);
        var queuedPayload = await queued.Content.ReadAsStringAsync();
        queued.StatusCode.Should().Be(HttpStatusCode.Accepted, queuedPayload);

        using var queuedJson = JsonDocument.Parse(queuedPayload);
        var jobId = queuedJson.RootElement.GetProperty("jobId").GetString();
        jobId.Should().NotBeNullOrWhiteSpace();

        using var denied = await _fixture.Client.GetAsync($"/api/v1/admin/import/jobs/{jobId}");
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await denied.Content.ReadAsStringAsync()).Should().NotContain(tableName)
            .And.NotContain("job-status.geojson").And.NotContain("currentPhase");

        var response = await _client.GetAsync($"/api/v1/admin/import/jobs/{jobId}");

        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);

        using var job = JsonDocument.Parse(payload);
        job.RootElement.GetProperty("jobId").GetString().Should().Be(jobId);
        job.RootElement.GetProperty("tableName").GetString().Should().Be(
            tableName,
            "the status must describe the job that was queued, not an arbitrary job");
        job.RootElement.GetProperty("fileName").GetString().Should().Be("job-status.geojson");
        job.RootElement.GetProperty("currentPhase").GetString().Should().NotBeNullOrWhiteSpace();
    }

}
