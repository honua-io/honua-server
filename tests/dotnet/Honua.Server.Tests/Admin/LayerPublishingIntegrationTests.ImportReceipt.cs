// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.Import, Operations.Create, Operations.Query)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [Endpoint("GET /api/v1/admin/import/jobs/{jobId}")]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("PUT /api/v1/admin/connections/{id}/layers/{layerId}/enabled")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task BackgroundImport_CompletionReceipt_PublishesWithoutDiscoveringPhysicalIdentifiers()
    {
        var jobId = await UploadReceiptImportAsync();
        var receipt = await WaitForCompletedImportReceiptAsync(jobId);
        receipt.GetProperty("featuresProcessed").GetInt32().Should().Be(1);
        receipt.TryGetProperty("schema", out var schema).Should().BeTrue("completed jobs must identify their physical schema");
        receipt.TryGetProperty("physicalTableName", out var table).Should().BeTrue("the logical table name is not a publication receipt");
        receipt.TryGetProperty("connectionId", out var connectionId).Should().BeTrue("publication needs a registered connection reference");
        _importedTableSchema = schema.GetString();
        _importedTableName = table.GetString();
        _importedTableSchema.Should().NotBeNullOrWhiteSpace();
        _importedTableName.Should().NotBeNullOrWhiteSpace();
        connectionId.GetString().Should().Be(_connectionId.ToString());
        var published = await PublishLayerAsync(new PublishLayerRequest
        {
            Schema = _importedTableSchema!,
            Table = _importedTableName!,
            LayerName = "Background receipt",
            PrimaryKey = "id",
            Fields = ["id", "name"],
            ServiceName = _serviceName,
            Enabled = false
        }, connectionId.GetString());
        _layerId = published.LayerId;
        published.Enabled.Should().BeFalse();
        var route = $"/api/v1/admin/connections/{connectionId.GetString()}/layers/{published.LayerId}/enabled?serviceName={_serviceName}";
        using var enabled = await _client.PutAsync(route,
            JsonContent.Create(new LayerEnabledRequest { Enabled = true }, options: _jsonOptions));
        enabled.StatusCode.Should().Be(HttpStatusCode.OK, await enabled.Content.ReadAsStringAsync());
        using var items = await GetGeometryJsonAsync($"/ogc/features/collections/{published.LayerId}/items?f=json");
        items.RootElement.GetProperty("features").GetArrayLength().Should().Be(1);
        items.RootElement.GetProperty("features")[0].GetProperty("properties").GetProperty("name").GetString()
            .Should().Be("Receipt row");
    }

    private async Task<string> UploadReceiptImportAsync()
    {
        await InstallImportFunctionsAsync();
        await DeleteSecureConnectionAsync();
        _connectionCreated = false;
        // The fixture already registers its catalog connection. A second registration
        // of the same database would deliberately make connection discovery ambiguous.
        var fixtureConnectionId = await _fixture.GetTestSecureConnectionIdAsync();
        fixtureConnectionId.Should().HaveValue();
        _connectionId = fixtureConnectionId!.Value;
        using var upload = new MultipartFormDataContent();
        upload.Add(new StringContent("""
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"name":"Receipt row"}}]}
            """), "File", "receipt.geojson");
        var logicalTableName = $"receipt_{Guid.NewGuid():N}"[..30];
        // Keep cleanup independent of the receipt assertions, including their expected failure.
        _importedTableSchema = _fixture.CurrentSchema;
        _importedTableName = $"imported_{logicalTableName}";
        upload.Add(new StringContent(logicalTableName), "TableName");
        upload.Add(new StringContent(_importedTableSchema!), "TargetSchema");
        upload.Add(new StringContent("4326"), "TargetSrid");
        upload.Add(new StringContent("true"), "ForceBackground");
        using var accepted = await _client.PostAsync("/api/v1/admin/import/upload", upload);
        var acceptedPayload = await accepted.Content.ReadAsStringAsync();
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted, acceptedPayload);
        using var acceptedDocument = JsonDocument.Parse(acceptedPayload);
        return acceptedDocument.RootElement.GetProperty("jobId").GetString()!;
    }

    private async Task<JsonElement> WaitForCompletedImportReceiptAsync(string jobId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            using var response = await _client.GetAsync($"/api/v1/admin/import/jobs/{jobId}", timeout.Token);
            var payload = await response.Content.ReadAsStringAsync(timeout.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
            using var document = JsonDocument.Parse(payload);
            var progress = document.RootElement.Deserialize<ImportProgress>(_jsonOptions)!;
            if (progress.Status is ImportStatus.Completed or ImportStatus.Failed or ImportStatus.Cancelled)
            {
                progress.Status.Should().Be(ImportStatus.Completed, payload);
                return document.RootElement.Clone();
            }

            await Task.Delay(100, timeout.Token);
        }
    }
}
