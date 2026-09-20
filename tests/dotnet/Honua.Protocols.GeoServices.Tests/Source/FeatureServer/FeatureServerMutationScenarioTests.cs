// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Certification-depth GeoServices protocol mutation scenarios. A new test class instance
/// creates a new database schema, preventing writes from leaking between cases.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class FeatureServerMutationScenarioTests : IAsyncLifetime
{
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.EnableV2ServiceEditingCapabilities(
            TestServiceId,
            ["Create", "Update", "Delete"]);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.BulkCreate, Operations.BulkUpdate, Operations.BulkDelete)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/applyEdits")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/addFeatures")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/updateFeatures")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/deleteFeatures")]
    public async Task MutationEndpoints_AcceptValidEdits_AndRoundTripEachState()
    {
        const string serviceApplyPayload = """
            [{"id":0,"adds":[{"attributes":{"name":"mutation-service-apply"},"geometry":{"x":-122.41,"y":37.77}}]}]
            """;
        var serviceApplyResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/applyEdits",
            serviceApplyPayload);
        serviceApplyResponse.Be200Ok();
        using var serviceDocument = JsonDocument.Parse(await serviceApplyResponse.Content.ReadAsStringAsync());
        var serviceAdd = serviceDocument.RootElement.GetProperty("editResults")[0].GetProperty("addResults")[0];
        serviceAdd.GetProperty("success").GetBoolean().Should().BeTrue();
        var serviceObjectId = serviceAdd.GetProperty("objectId").GetInt64();
        await AssertFeatureNameAsync(serviceObjectId, "mutation-service-apply");

        const string layerApplyPayload = """
            {"adds":[{"attributes":{"name":"mutation-layer-apply"},"geometry":{"x":-122.42,"y":37.78}}]}
            """;
        var layerApplyResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits",
            layerApplyPayload);
        var layerApply = await DeserializeEditsAsync(layerApplyResponse);
        layerApply.AddResults.Should().ContainSingle(result => result.Success);
        var layerObjectId = layerApply.AddResults![0].ObjectId!.Value;
        await AssertFeatureNameAsync(layerObjectId, "mutation-layer-apply");

        const string addPayload = """
            {"features":[{"attributes":{"name":"mutation-added"},"geometry":{"x":-122.43,"y":37.79}}]}
            """;
        var addResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/addFeatures",
            addPayload);
        var addResult = await DeserializeEditsAsync(addResponse);
        addResult.AddResults.Should().ContainSingle(result => result.Success);
        var objectId = addResult.AddResults![0].ObjectId!.Value;
        await AssertFeatureNameAsync(objectId, "mutation-added");

        var updatePayload = $$$"""
            {"features":[{"attributes":{"objectid":{{{objectId}}},"name":"mutation-updated"}}],"rollbackOnFailure":true}
            """;
        var updateResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/updateFeatures",
            updatePayload);
        var updateResult = await DeserializeEditsAsync(updateResponse);
        updateResult.UpdateResults.Should().ContainSingle(result => result.Success && result.ObjectId == objectId);
        await AssertFeatureNameAsync(objectId, "mutation-updated");

        var deletePayload = $$$"""{"objectIds":[{{{objectId}}}]}""";
        var deleteResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/deleteFeatures",
            deletePayload);
        var deleteResult = await DeserializeEditsAsync(deleteResponse);
        deleteResult.DeleteResults.Should().ContainSingle(result => result.Success && result.ObjectId == objectId);
        (await ReadFeatureCountAsync(objectId)).Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/applyEdits")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/addFeatures")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/updateFeatures")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/deleteFeatures")]
    public async Task MutationEndpoints_RejectInvalidEdits_WithoutChangingStoredState()
    {
        var originalId = await _fixture.InsertFeatureAsync(TestLayerId, "mutation-original");

        const string malformedServiceApply = """[{"id":0,"adds":[{"attributes":{"name":"invalid-service-apply"}}]""";
        var serviceResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/applyEdits",
            malformedServiceApply);
        await serviceResponse.AssertGeoServicesErrorAsync(400);
        (await ReadCountByNameAsync("invalid-service-apply")).Should().Be(0);

        const string invalidLayerApply = """
            {"adds":[{"attributes":{"name":"invalid-layer-apply"},"geometry":{"rings":[[[-122,37],[-122,38],[-121,38],[-122,37]]]}}]}
            """;
        var layerResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits",
            invalidLayerApply);
        var layerResult = await DeserializeEditsAsync(layerResponse);
        layerResult.AddResults.Should().ContainSingle(result => !result.Success && result.Error != null);
        (await ReadCountByNameAsync("invalid-layer-apply")).Should().Be(0);

        const string invalidAdd = """
            {"features":[{"attributes":{"name":"invalid-add"},"geometry":{"rings":[[[-122,37],[-122,38],[-121,38],[-122,37]]]}}]}
            """;
        var addResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/addFeatures",
            invalidAdd);
        var addResult = await DeserializeEditsAsync(addResponse);
        addResult.AddResults.Should().ContainSingle(result => !result.Success && result.Error != null);
        (await ReadCountByNameAsync("invalid-add")).Should().Be(0);

        const string invalidUpdate = """
            {"features":[{"attributes":{"objectid":999999999,"name":"invalid-update"}}],"rollbackOnFailure":true}
            """;
        var updateResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/updateFeatures",
            invalidUpdate);
        var updateResult = await DeserializeEditsAsync(updateResponse);
        updateResult.UpdateResults.Should().ContainSingle(result => !result.Success && result.Error != null);
        await AssertFeatureNameAsync(originalId, "mutation-original");

        const string invalidDelete = """{"objectIds":"1,,2"}""";
        var deleteResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/deleteFeatures",
            invalidDelete);
        await deleteResponse.AssertGeoServicesErrorAsync(400);
        await AssertFeatureNameAsync(originalId, "mutation-original");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task ApplyEdits_ConcurrentAttributeOnlyUpdates_PreserveGeometryAndReportSuccess()
    {
        const string path = "/rest/services/test/FeatureServer/0/applyEdits";
        using var add = await PostJsonAsync(path,
            """{"adds":[{"attributes":{"name":"before"},"geometry":{"x":-122.25,"y":37.75,"spatialReference":{"wkid":4326}}}]}""");
        var added = await DeserializeEditsAsync(add);
        var objectId = added.AddResults.Should().ContainSingle(result => result.Success).Subject.ObjectId!.Value;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json",
                ["updates"] = $$$"""[{"attributes":{"objectid":{{{objectId}}},"name":"after"}}]"""
            });
            using var response = await _fixture.Client.PostAsync(path, form);
            var result = await DeserializeEditsAsync(response);
            result.UpdateResults.Should().ContainSingle(edit => edit.Success && edit.ObjectId == objectId,
                await response.Content.ReadAsStringAsync());
            result.UpdateResults![0].Error.Should().BeNull();
        }));

        using var query = await _fixture.Client.GetAsync(
            $"/rest/services/test/FeatureServer/0/query?f=json&objectIds={objectId}&outFields=*&returnGeometry=true&outSR=4326");
        query.Be200Ok();
        using var document = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
        var feature = document.RootElement.GetProperty("features").EnumerateArray().Single();
        feature.GetProperty("attributes").GetProperty("name").GetString().Should().Be("after");
        feature.GetProperty("geometry").GetProperty("x").GetDouble().Should().Be(-122.25);
        feature.GetProperty("geometry").GetProperty("y").GetDouble().Should().Be(37.75);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task ApplyEdits_ConcurrentDistinctPartialUpdates_RereadAfterLockedSnapshotChanges()
    {
        _fixture.UpdateV2ResourceSchemaField(0, new MetadataV2Field { Name = "score", Type = MetadataV2FieldType.Integer });
        const string path = "/rest/services/test/FeatureServer/0/applyEdits";
        using var add = await PostJsonAsync(path,
            """{"adds":[{"attributes":{"name":"before","score":0},"geometry":{"x":-122.25,"y":37.75,"spatialReference":{"wkid":4326}}}]}""");
        var added = await DeserializeEditsAsync(add);
        var objectId = added.AddResults.Should().ContainSingle(result => result.Success).Subject.ObjectId!.Value;
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        using var builder = new Npgsql.NpgsqlCommandBuilder();
        var schema = builder.QuoteIdentifier(_fixture.CurrentSchema!);
        await using (var rowLock = new Npgsql.NpgsqlCommand($"SELECT objectid FROM {schema}.features WHERE layer_id=0 AND objectid=@id FOR UPDATE", connection, transaction))
        {
            rowLock.Parameters.AddWithValue("id", objectId);
            await rowLock.ExecuteScalarAsync();
        }

        // Hold the stored row while all three HTTP requests read and assemble their
        // partial updates. Their write transactions must then wait on this lock.
        var requests = new[]
        {
            $$$"""{"updates":[{"attributes":{"objectid":{{{objectId}}},"name":"after"}}]}""",
            $$$"""{"updates":[{"attributes":{"objectid":{{{objectId}}},"score":29}}]}""",
            $$$$"""{"updates":[{"attributes":{"objectid":{{{{objectId}}}}},"geometry":{"x":-120.5,"y":36.5,"spatialReference":{"wkid":4326}}}]}"""
        };
        var pending = requests.Select(payload => PostJsonAsync(path, payload)).ToArray();
        var waiting = 0L;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (waiting < 3 && DateTime.UtcNow < deadline)
            {
                await using (var refresh = new Npgsql.NpgsqlCommand("SELECT pg_stat_clear_snapshot()", connection, transaction))
                {
                    await refresh.ExecuteNonQueryAsync();
                }
                await using var command = new Npgsql.NpgsqlCommand(
                    """
                    WITH RECURSIVE blocked(pid) AS (
                        SELECT pid FROM pg_stat_activity WHERE @lockPid = ANY(pg_blocking_pids(pid))
                        UNION
                        SELECT activity.pid FROM pg_stat_activity activity
                        JOIN blocked ON blocked.pid = ANY(pg_blocking_pids(activity.pid))
                    ) SELECT count(*) FROM blocked
                    """, connection, transaction);
                command.Parameters.AddWithValue("lockPid", connection.ProcessID);
                waiting = (long)(await command.ExecuteScalarAsync())!;
                if (waiting < 3)
                {
                    await Task.Delay(25);
                }
            }
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        foreach (var response in await Task.WhenAll(pending))
        {
            using (response)
            {
                var result = await DeserializeEditsAsync(response);
                result.UpdateResults.Should().ContainSingle(edit => edit.Success && edit.ObjectId == objectId,
                    await response.Content.ReadAsStringAsync());
            }
        }
        waiting.Should().BeGreaterThanOrEqualTo(3, "all partial requests must assemble against the same blocked row");
        using var query = await _fixture.Client.GetAsync(
            $"/rest/services/test/FeatureServer/0/query?f=json&objectIds={objectId}&outFields=*&returnGeometry=true&outSR=4326");
        query.Be200Ok();
        using var document = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
        var feature = document.RootElement.GetProperty("features").EnumerateArray().Single();
        feature.GetProperty("attributes").GetProperty("name").GetString().Should().Be("after");
        feature.GetProperty("attributes").GetProperty("score").GetInt32().Should().Be(29);
        feature.GetProperty("geometry").GetProperty("x").GetDouble().Should().Be(-120.5);
        feature.GetProperty("geometry").GetProperty("y").GetDouble().Should().Be(36.5);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.Query, Operations.Metadata)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task ApplyEdits_NumericPrimaryKeyAndDuplicateIdAttribute_TargetsOnlyPrimaryKey()
    {
        _fixture.UpdateV2ResourceSchemaField(0, new MetadataV2Field
        {
            Name = "objectid",
            Type = MetadataV2FieldType.BigInteger,
            Nullable = false
        });
        _fixture.UpdateV2ResourceSchemaField(0, new MetadataV2Field
        {
            Name = "gid",
            Type = MetadataV2FieldType.BigInteger,
            SemanticRoles = ["id.primary"]
        });
        _fixture.UpdateV2ResourceSchemaField(0, new MetadataV2Field
        {
            Name = "id",
            Type = MetadataV2FieldType.Integer
        });
        const string path = "/rest/services/test/FeatureServer/0/applyEdits";
        // Represent a published source whose physical primary keys are 701/702 and
        // whose unrelated id attribute is duplicated. Canonical storage names the
        // physical key objectid; the resource publishes that key as gid. applyEdits
        // inserts allocate keys and cannot create this pre-existing-key fixture.
        await using (var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync())
        await using (var seed = connection.CreateCommand())
        {
            using var identifierBuilder = new Npgsql.NpgsqlCommandBuilder();
            var schema = identifierBuilder.QuoteIdentifier(_fixture.CurrentSchema!);
            seed.CommandText = $$"""
                INSERT INTO {{schema}}.features (layer_id, objectid, geometry, attributes)
                VALUES (0,701,ST_SetSRID(ST_Point(-122.25,37.75),4326),'{"gid":701,"id":0,"name":"first"}'::jsonb),
                       (0,702,ST_SetSRID(ST_Point(-121.25,38.75),4326),'{"gid":702,"id":0,"name":"second"}'::jsonb)
                """;
            await seed.ExecuteNonQueryAsync();
        }

        using var metadataResponse = await _fixture.Client.GetAsync("/rest/services/test/FeatureServer/0?f=json");
        metadataResponse.Be200Ok();
        using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync());
        metadata.RootElement.GetProperty("objectIdField").GetString().Should().Be("gid");

        using var update = await PostJsonAsync(path,
            """{"updates":[{"attributes":{"gid":701,"name":"changed"}}]}""");
        var updated = await DeserializeEditsAsync(update);
        updated.UpdateResults.Should().ContainSingle(result => result.Success && result.ObjectId == 701,
            await update.Content.ReadAsStringAsync());
        await AssertFeatureNameAsync(701, "changed");
        await AssertFeatureNameAsync(702, "second");
        (await ReadFeatureCountAsync(0)).Should().Be(0);

        using var remove = await PostJsonAsync(path, """{"deletes":[701]}""");
        var removed = await DeserializeEditsAsync(remove);
        removed.DeleteResults.Should().ContainSingle(result => result.Success && result.ObjectId == 701);
        (await ReadFeatureCountAsync(701)).Should().Be(0);
        await AssertFeatureNameAsync(702, "second");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_InheritedSharedTableBinding_IsolatesRequestedObjectIdsByLayer()
    {
        await using (var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync())
        await using (var seed = connection.CreateCommand())
        {
            using var identifierBuilder = new Npgsql.NpgsqlCommandBuilder();
            var schema = identifierBuilder.QuoteIdentifier(_fixture.CurrentSchema!);
            seed.CommandText = $$"""
                INSERT INTO {{schema}}.features (layer_id, objectid, geometry, attributes)
                VALUES (0,17001,NULL,'{"name":"layer-zero-only"}'::jsonb),
                       (1,17002,NULL,'{"name":"layer-one-only"}'::jsonb)
                """;
            await seed.ExecuteNonQueryAsync();
        }

        foreach (var layer in new[] { 0, 1 })
        {
            var path = $"/rest/services/test/FeatureServer/{layer}/query?f=json&objectIds=17001,17002";
            using var response = await _fixture.Client.GetAsync(path + "&outFields=name&returnGeometry=false");
            response.Be200Ok();
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            document.RootElement.TryGetProperty("error", out _).Should().BeFalse(body);
            var feature = document.RootElement.GetProperty("features").EnumerateArray().Should().ContainSingle().Which;
            feature.GetProperty("attributes").GetProperty("name").GetString()
                .Should().Be(layer == 0 ? "layer-zero-only" : "layer-one-only");
            using var countResponse = await _fixture.Client.GetAsync(path + "&returnCountOnly=true");
            countResponse.Be200Ok();
            using var countDocument = JsonDocument.Parse(await countResponse.Content.ReadAsStringAsync());
            countDocument.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        }
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string path, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _fixture.Client.PostAsync(path, content);
    }

    private static async Task<ApplyEditsResponse> DeserializeEditsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize(body, FeatureServerJsonContext.Default.ApplyEditsResponse)
            ?? throw new InvalidOperationException("Expected an apply-edits response.");
    }

    private async Task AssertFeatureNameAsync(long objectId, string expectedName)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=json&objectIds={objectId}&returnGeometry=true");
        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var feature = document.RootElement.GetProperty("features").EnumerateArray().Single();
        feature.GetProperty("attributes").GetProperty("name").GetString().Should().Be(expectedName);
    }

    private async Task<int> ReadFeatureCountAsync(long objectId)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=json&objectIds={objectId}&returnCountOnly=true");
        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("count").GetInt32();
    }

    private async Task<int> ReadCountByNameAsync(string name)
    {
        var where = Uri.EscapeDataString($"name='{name}'");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=json&where={where}&returnCountOnly=true");
        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("count").GetInt32();
    }

    [IntegrationTest]
    [Operation(Operations.BulkCreate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/addFeatures")]
    public async Task AddFeatures_WithNullSystemMaintainedObjectId_AssignsOneInstead()
    {
        // QGIS serialises the unset object id as an explicit null when it
        // digitises a new feature, and there is no way for the client to omit it.
        // The non-nullable check used to fail the whole edit with error 1006,
        // "Field 'objectid' cannot be null", so no stock desktop digitizing
        // session could edit a FeatureServer at all. objectid is declared
        // editable:false with uniqueIdField.isSystemMaintained:true, so a
        // supplied null can only mean "not supplied".
        const string nullOidPayload = """
            {"features":[{"attributes":{"name":"null-oid-insert","objectid":null},"geometry":{"x":-122.44,"y":37.80}}]}
            """;

        var response = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/addFeatures",
            nullOidPayload);

        var result = await DeserializeEditsAsync(response);
        result.AddResults.Should().ContainSingle(edit => edit.Success,
            "a null system-maintained object id means 'assign one', not 'set null'");
        var assigned = result.AddResults![0].ObjectId!.Value;
        assigned.Should().BeGreaterThan(0,
            "the store must assign a real object id rather than persisting the null");
        await AssertFeatureNameAsync(assigned, "null-oid-insert");
    }

    [IntegrationTest]
    [Operation(Operations.BulkUpdate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/updateFeatures")]
    public async Task UpdateFeatures_WithNullSystemMaintainedObjectId_IsStillRejected()
    {
        // The insert relaxation must not leak into update: there a
        // non-editable field is a genuine error, because the caller is
        // addressing an existing row rather than asking for an assignment.
        const string seedPayload = """
            {"features":[{"attributes":{"name":"null-oid-update-seed"},"geometry":{"x":-122.45,"y":37.81}}]}
            """;
        var seed = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/addFeatures",
            seedPayload);
        var seeded = await DeserializeEditsAsync(seed);
        seeded.AddResults.Should().ContainSingle(edit => edit.Success);

        var updatePayload = $$$"""
            {"features":[{"attributes":{"objectid":null,"name":"null-oid-update"}}]}
            """;
        var response = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/updateFeatures",
            updatePayload);

        var result = await DeserializeEditsAsync(response);
        result.UpdateResults.Should().NotBeNullOrEmpty();
        result.UpdateResults!.Should().OnlyContain(edit => !edit.Success,
            "an update carrying a null object id addresses no row and must not succeed");
    }
}
