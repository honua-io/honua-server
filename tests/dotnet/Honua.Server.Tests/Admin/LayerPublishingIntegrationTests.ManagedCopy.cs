// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishManagedCopy_WithDifferentFeatureStoreSearchPath_IsRejected()
    {
        // The connection resolves public.features; the writer resolves its isolated
        // fixture table. Matching database credentials alone must not permit this.
        using var body = System.Net.Http.Json.JsonContent.Create(new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = $"Mismatched {_serviceName}",
            ServiceName = _serviceName,
            GeometryColumn = "geom",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            CreateEditableCopy = true
        }, options: _jsonOptions);
        var response = await _client.PostAsync($"/api/v1/admin/connections/{_connectionId}/layers", body);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    [Operation(Operations.Create)]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task PublishManagedCopy_FromUploadedData_WritesAndReadsTheSameRows()
    {
        // All feature data and metadata for this collection are created through
        // product APIs. The fixture's unrelated source tables are not used.
        await DeleteSecureConnectionAsync();
        _connectionCreated = false;
        var publishConnection = new NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString)
        {
            SearchPath = $"{_fixture.CurrentSchema},public"
        };
        await CreateSecureConnectionAsync(publishConnection.ConnectionString);
        const string source = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[-122.4,37.7]},"properties":{"name":"Imported"}}
            ]}
            """;
        using var upload = new MultipartFormDataContent();
        var file = new StringContent(source, Encoding.UTF8, "application/geo+json");
        file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "managed-copy.geojson"
        };
        upload.Add(file);
        upload.Add(new StringContent($"managed_copy_{Guid.NewGuid():N}"), "TableName");
        upload.Add(new StringContent("4326"), "TargetSrid");
        var uploadResponse = await _client.PostAsync("/api/v1/admin/import/upload", upload);
        var uploadPayload = await uploadResponse.Content.ReadAsStringAsync();
        uploadResponse.StatusCode.Should().Be(HttpStatusCode.OK, uploadPayload);
        var imported = JsonSerializer.Deserialize<ImportResult>(uploadPayload, _jsonOptions)!;
        imported.Success.Should().BeTrue(uploadPayload);
        _importedTableSchema = imported.Schema;
        _importedTableName = imported.PhysicalTableName;
        using var discovery = JsonDocument.Parse(await _client.GetStringAsync($"/api/v1/admin/connections/{_connectionId}/tables"));
        var table = discovery.RootElement.GetProperty("tables").EnumerateArray().Single(item =>
            item.GetProperty("schema").GetString() == _importedTableSchema && item.GetProperty("table").GetString() == _importedTableName);
        var published = await PublishLayerAsync(new PublishLayerRequest
        {
            Schema = _importedTableSchema!,
            Table = _importedTableName!,
            LayerName = $"Managed {_serviceName}",
            ServiceName = _serviceName,
            GeometryColumn = table.GetProperty("geometryColumn").GetString(),
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = ["id", "properties"],
            CreateEditableCopy = true
        }, _connectionName);
        _layerId = published.LayerId;
        var featureServer = $"/rest/services/{_serviceName}/FeatureServer/{_layerId}";
        var collection = $"/ogc/features/collections/{_layerId}/items";
        using var initial = JsonDocument.Parse(await _client.GetStringAsync($"{featureServer}/query?f=json&where=1%3D1&outFields=*&returnGeometry=true"));
        var initialFeatures = initial.RootElement.GetProperty("features");
        initialFeatures.GetArrayLength().Should().Be(1);
        var attributes = initialFeatures[0].GetProperty("attributes");
        attributes.GetProperty("honua_source_id").GetInt64().Should().BeGreaterThan(0);
        var importedTargetId = attributes.GetProperty("id").GetInt64();
        using var metadata = JsonDocument.Parse(await _client.GetStringAsync($"{featureServer}?f=json"));
        metadata.RootElement.GetProperty("fields").EnumerateArray()
            .Single(field => field.GetProperty("name").GetString() == "id")
            .GetProperty("editable").GetBoolean().Should().BeFalse();

        const string createdBody = """
            {"type":"Feature","geometry":{"type":"Point","coordinates":[-122.3,37.8]},"properties":{"properties":{"name":"Created"}}}
            """;
        var createdResponse = await _client.PostAsync(collection, new StringContent(createdBody, Encoding.UTF8, "application/geo+json"));
        var createdPayload = await createdResponse.Content.ReadAsStringAsync();
        createdResponse.StatusCode.Should().Be(HttpStatusCode.Created, createdPayload);
        createdResponse.Headers.Location.Should().NotBeNull();
        var createdLocation = createdResponse.Headers.Location!.ToString();
        using var createdReadback = JsonDocument.Parse(await _client.GetStringAsync(createdLocation));
        createdReadback.RootElement.GetProperty("properties").GetProperty("properties").GetProperty("name").GetString().Should().Be("Created");
        var createdId = createdReadback.RootElement.GetProperty("id").ToString();
        using var byPrimaryId = JsonDocument.Parse(await _client.GetStringAsync(
            $"{featureServer}/query?f=json&where=id%3D{Uri.EscapeDataString(createdId)}&outFields=*&returnGeometry=false"));
        byPrimaryId.RootElement.GetProperty("features").GetArrayLength().Should().Be(1);

        // Accepting the addressing ID must not grant writes to other read-only fields.
        using var identityMutation = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["updates"] = JsonSerializer.Serialize(new[]
            {
                new { attributes = new Dictionary<string, object?> { ["id"] = importedTargetId, ["honua_source_id"] = -1 } }
            }),
            ["rollbackOnFailure"] = "true"
        });
        var rejectedIdentity = await _client.PostAsync($"{featureServer}/applyEdits", identityMutation);
        var rejectedPayload = await rejectedIdentity.Content.ReadAsStringAsync();
        rejectedIdentity.StatusCode.Should().Be(HttpStatusCode.OK, rejectedPayload);
        using var rejected = JsonDocument.Parse(rejectedPayload);
        rejected.RootElement.GetProperty("updateResults")[0].GetProperty("success").GetBoolean().Should().BeFalse();
        rejected.RootElement.GetProperty("updateResults")[0].GetProperty("error").GetProperty("description")
            .GetString().Should().Contain("honua_source_id");

        var updates = JsonSerializer.Serialize(new[]
        {
            new
            {
                attributes = new Dictionary<string, object?>
                {
                    ["id"] = importedTargetId,
                    ["properties"] = new { name = "Updated imported feature" }
                }
            }
        });
        using var updateForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["updates"] = updates,
            ["rollbackOnFailure"] = "true"
        });
        var updatedResponse = await _client.PostAsync($"{featureServer}/applyEdits", updateForm);
        var updatedPayload = await updatedResponse.Content.ReadAsStringAsync();
        updatedResponse.StatusCode.Should().Be(HttpStatusCode.OK, updatedPayload);
        using var updated = JsonDocument.Parse(updatedPayload);
        updated.RootElement.GetProperty("updateResults")[0].GetProperty("success").GetBoolean().Should().BeTrue(updatedPayload);
        using var readback = JsonDocument.Parse(await _client.GetStringAsync($"{collection}/{importedTargetId}"));
        readback.RootElement.GetProperty("properties").GetProperty("properties").GetProperty("name").GetString().Should().Be("Updated imported feature");
        readback.RootElement.GetProperty("properties").GetProperty("honua_source_id").GetInt64().Should().Be(attributes.GetProperty("honua_source_id").GetInt64());

        // The source-snapshot refresh operation must never overwrite managed edits.
        var refresh = await _client.PostAsync($"/api/v1/admin/connections/{_connectionId}/layers/{_layerId}/features/refresh", null);
        refresh.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var afterRefresh = JsonDocument.Parse(await _client.GetStringAsync($"{collection}/{importedTargetId}"));
        afterRefresh.RootElement.GetProperty("properties").GetProperty("properties").GetProperty("name").GetString().Should().Be("Updated imported feature");

        using var deleteForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["deletes"] = importedTargetId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["rollbackOnFailure"] = "true"
        });
        var deletedResponse = await _client.PostAsync($"{featureServer}/applyEdits", deleteForm);
        var deletedPayload = await deletedResponse.Content.ReadAsStringAsync();
        deletedResponse.StatusCode.Should().Be(HttpStatusCode.OK, deletedPayload);
        using var deleted = JsonDocument.Parse(deletedPayload);
        deleted.RootElement.GetProperty("deleteResults")[0].GetProperty("success").GetBoolean().Should().BeTrue(deletedPayload);
        (await _client.GetAsync($"{collection}/{importedTargetId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var finalQuery = JsonDocument.Parse(await _client.GetStringAsync($"{featureServer}/query?f=json&where=1%3D1&outFields=*&returnGeometry=true"));
        finalQuery.RootElement.GetProperty("features").GetArrayLength().Should().Be(1);
        finalQuery.RootElement.GetProperty("features")[0].GetProperty("attributes").GetProperty("properties").GetProperty("name").GetString().Should().Be("Created");
    }
}
