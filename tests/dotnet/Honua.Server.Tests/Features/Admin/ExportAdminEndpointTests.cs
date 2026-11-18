// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Exports;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class ExportEndpointTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/exports/{exportId}")]
    [Endpoint("GET /api/v1/admin/exports")]
    public async Task GetExport_AfterTwoVersions_DescribesBothWithSignedInUrls()
    {
        var first = await PublishAsync(IdentityField);
        var second = await PublishAsync(IdentityField);

        var export = await GetExportAsync();

        Text(export, "status").Should().Be(ExportResponse.Ready);
        Text(export, "currentVersion").Should().Be(second.Version);
        Text(export, "collectionUrl").Should().EndWith($"/api/v1/exports/{_exportId}/{second.Version}/collection.json");
        Text(export, "notPublicReason").Should().NotBeNullOrEmpty();
        export.GetProperty("versions").EnumerateArray().Select(version => Text(version, "version"))
            .Should().Equal(first.Version, second.Version);
        export.GetProperty("versions")[1].GetProperty("artifacts").EnumerateArray().Select(artifact => Text(artifact, "kind"))
            .Should().BeEquivalentTo("collection", "geoparquet", "pmtiles");

        using var list = JsonDocument.Parse(await _client.GetStringAsync(ExportsRoute));
        list.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()
            .Should().Contain(item => Text(item, "id") == _exportId);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/exports/{exportId}")]
    [Endpoint("GET /api/v1/exports/{exportId}/current.json")]
    public async Task DeleteExport_Published_StopsServingAndKeepsItsVersions()
    {
        var published = await PublishAsync(IdentityField);

        using var deleted = await _client.DeleteAsync($"{ExportsRoute}/{_exportId}");

        deleted.StatusCode.Should().Be(HttpStatusCode.OK);
        var export = await GetExportAsync();
        Text(export, "status").Should().Be(ExportResponse.Unpublished);
        export.GetProperty("versions").EnumerateArray().Select(version => Text(version, "version")).Should().Equal(published.Version);
        using var current = await _client.GetAsync($"/api/v1/exports/{_exportId}/current.json");
        current.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/exports/{exportId}")]
    [Endpoint("DELETE /api/v1/admin/exports/{exportId}")]
    public async Task GetAndDeleteExport_Unknown_ReturnNotFound()
    {
        using var get = await _client.GetAsync($"{ExportsRoute}/never-exported");
        using var delete = await _client.DeleteAsync($"{ExportsRoute}/never-exported");

        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        delete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/exports")]
    public async Task CreateExport_Started_ReturnsRunningExportWithItsJob()
    {
        using var response = await _client.PostAsJsonAsync(ExportsRoute, PublishBody(IdentityField));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location!.OriginalString.Should().EndWith($"/api/v1/admin/exports/{_exportId}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var export = body.RootElement.GetProperty("data");
        Text(export, "status").Should().Be(ExportResponse.Running);
        Text(export, "jobId").Should().NotBeNullOrEmpty();
        await WaitForJobAsync(Text(export, "jobId"));
    }

    private async Task<JsonElement> GetExportAsync()
    {
        using var response = await _client.GetAsync($"{ExportsRoute}/{_exportId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("data").Clone();
    }

    private async Task WaitForJobAsync(string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            using var json = JsonDocument.Parse(await _client.GetStringAsync($"{JobsRoute}/{jobId}"));
            if (Property(json.RootElement, "status").GetInt32() >= 2)
            {
                return;
            }

            await Task.Delay(200);
        }
    }

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()!;
}
