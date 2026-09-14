// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// #4019: a replica scope or download backlog larger than the per-layer limit used to be a permanent
/// 400. It is now delivered in bounded generation windows. The host runs with
/// <c>Limits:Query:MaxRecordCount=100</c> and <c>Limits:Replica:MaxChangesPerLayer=250</c>; the expected
/// rows are read back from the database with SQL, independently of the replica API.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerReplicaDeliveryWindowTests : IAsyncLifetime
{
    private const int MaxChangesPerLayer = 250;

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ConfigureWebHost(builder =>
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Limits:Query:MaxRecordCount"] = "100",
                ["Limits:Query:DefaultRecordCount"] = "100",
                ["Limits:Replica:MaxChangesPerLayer"] = MaxChangesPerLayer.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        });
    });

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.UpdateV2ServiceMetadata(WebAppFixture.TestServiceId, capabilities: ["Query", "Sync"]);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.CreateReplica, Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task CreateReplica_ScopeLargerThanQueryAndChangeLimits_DeliversEveryFeatureInBoundedWindows()
    {
        // 600 rows in an otherwise empty area: more than the 100-record query cap and the 250-change limit.
        // Explicit high object ids keep the rows distinct from every other fixture's change history.
        await InsertFeaturesAsync(firstObjectId: 7_410_000_000, count: 600, longitude: 170.0, latitude: -44.0);
        var expected = await ReadRowsAsync(7_410_000_000, 7_410_000_599);
        expected.Should().HaveCount(600);

        var created = await PostJsonAsync("createReplica", new
        {
            replicaName = "windowed-first-sync",
            layers = "0",
            syncModel = "perReplica",
            geometry = "{\"xmin\":169.9,\"ymin\":-44.1,\"xmax\":170.9,\"ymax\":-43.1}",
            geometryType = "esriGeometryEnvelope",
            inSR = "4326",
            f = "json"
        });
        var replicaId = created.GetProperty("replicaID").GetString()!;
        created.GetProperty("exceededTransferLimit").GetBoolean().Should().BeTrue(
            "600 in-scope rows cannot fit one 250-change window");

        var client = new Dictionary<long, (string? Name, double X, double Y)>();
        var windows = 1;
        ApplyAdds(client, created.GetProperty("layers")[0].GetProperty("features"));
        var cursor = created.GetProperty("serverGen").GetInt64();

        var exceeded = true;
        while (exceeded)
        {
            windows.Should().BeLessThan(40, "every window must advance the replica cursor");
            var synced = await DownloadAsync(replicaId, cursor);
            var layer = synced.GetProperty("edits").EnumerateArray().Should().ContainSingle().Subject;
            (layer.GetProperty("adds").GetInt32() + layer.GetProperty("updates").GetInt32() + layer.GetProperty("deletes").GetInt32())
                .Should().BeLessThanOrEqualTo(MaxChangesPerLayer);
            if (layer.TryGetProperty("addFeatures", out var addFeatures))
            {
                ApplyAdds(client, addFeatures);
            }

            var next = synced.GetProperty("serverGen").GetInt64();
            next.Should().BeGreaterThanOrEqualTo(cursor);
            exceeded = synced.TryGetProperty("exceededTransferLimit", out var flag) && flag.GetBoolean();
            if (exceeded)
            {
                next.Should().BeGreaterThan(cursor, "a windowed download must move the cursor");
            }

            cursor = next;
            windows++;
        }

        windows.Should().BeGreaterThanOrEqualTo(3, "600 rows at no more than 250 per window need at least three windows");
        client.Should().BeEquivalentTo(expected, "the windows together must deliver every scoped row exactly as stored");
    }

    [IntegrationTest]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_SmallScopeOnLayerWithLargeUnrelatedHistory_ReturnsTheWholeSnapshot()
    {
        // The window limit counts only changes the replica can receive. 300 changes outside the scope used to
        // narrow the window before scope filtering, which a syncModel=none snapshot cannot continue, so a
        // two-row snapshot was refused with 400.
        await InsertFeaturesAsync(firstObjectId: 7_430_000_000, count: 300, longitude: 160.0, latitude: -40.0);
        await InsertFeaturesAsync(firstObjectId: 7_431_000_000, count: 2, longitude: -60.0, latitude: -60.0);
        var expected = await ReadRowsAsync(7_431_000_000, 7_431_000_001);
        expected.Should().HaveCount(2);

        var created = await PostJsonAsync("createReplica", new
        {
            replicaName = "tiny-snapshot",
            layers = "0",
            syncModel = "none",
            geometry = "{\"xmin\":-60.5,\"ymin\":-60.5,\"xmax\":-59.5,\"ymax\":-59.5}",
            geometryType = "esriGeometryEnvelope",
            inSR = "4326",
            f = "json"
        });

        created.TryGetProperty("error", out _).Should().BeFalse(created.GetRawText());
        (created.TryGetProperty("exceededTransferLimit", out var flag) && flag.GetBoolean()).Should().BeFalse();
        var client = new Dictionary<long, (string? Name, double X, double Y)>();
        ApplyAdds(client, created.GetProperty("layers")[0].GetProperty("features"));
        client.Should().BeEquivalentTo(expected, "the snapshot must carry exactly the scoped rows as stored");
    }

    [IntegrationTest]
    [Operation(Operations.CreateReplica, Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_BacklogLargerThanChangeLimit_AdvancesInWindowsAndConverges()
    {
        const long firstObjectId = 7_420_000_000;
        var created = await PostJsonAsync("createReplica", new
        {
            replicaName = "windowed-backlog",
            layers = "0",
            syncModel = "perReplica",
            geometry = "{\"xmin\":-60.1,\"ymin\":-50.1,\"xmax\":-59.1,\"ymax\":-49.1}",
            geometryType = "esriGeometryEnvelope",
            inSR = "4326",
            f = "json"
        });
        var replicaId = created.GetProperty("replicaID").GetString()!;
        var cursor = created.GetProperty("serverGen").GetInt64();
        var client = new Dictionary<long, (string? Name, double X, double Y)>();

        // Drain whatever history the creation window did not reach before the backlog is written.
        var exceeded = created.TryGetProperty("exceededTransferLimit", out var createdFlag) && createdFlag.GetBoolean();
        while (exceeded)
        {
            var drained = await DownloadAsync(replicaId, cursor);
            cursor = drained.GetProperty("serverGen").GetInt64();
            exceeded = drained.TryGetProperty("exceededTransferLimit", out var flag) && flag.GetBoolean();
        }

        // The replica goes offline over a busy period: 600 inserts, 50 later renames, 20 later deletes.
        await InsertFeaturesAsync(firstObjectId, count: 600, longitude: -60.0, latitude: -50.0);
        await ExecuteSqlAsync(
            "UPDATE features SET attributes = jsonb_set(attributes, '{name}', to_jsonb('renamed-' || (objectid - @first)::text)) WHERE layer_id = 0 AND objectid BETWEEN @first AND @first + 49",
            firstObjectId);
        await ExecuteSqlAsync(
            "DELETE FROM features WHERE layer_id = 0 AND objectid BETWEEN @first + 100 AND @first + 119",
            firstObjectId);
        var expected = await ReadRowsAsync(firstObjectId, firstObjectId + 599);
        expected.Should().HaveCount(580);

        var windows = 0;
        exceeded = true;
        while (exceeded)
        {
            windows.Should().BeLessThan(40, "every window must advance the replica cursor");
            var synced = await DownloadAsync(replicaId, cursor);
            var layer = synced.GetProperty("edits").EnumerateArray().Should().ContainSingle().Subject;
            (layer.GetProperty("adds").GetInt32() + layer.GetProperty("updates").GetInt32() + layer.GetProperty("deletes").GetInt32())
                .Should().BeLessThanOrEqualTo(MaxChangesPerLayer);

            if (layer.TryGetProperty("addFeatures", out var addFeatures))
            {
                ApplyAdds(client, addFeatures);
            }

            if (layer.TryGetProperty("updateFeatures", out var updateFeatures))
            {
                foreach (var row in updateFeatures.EnumerateArray().Select(ReadFeature))
                {
                    client.Should().ContainKey(row.Id, "an update must never arrive for a row the client has not received");
                    client[row.Id] = (row.Name, row.X, row.Y);
                }
            }

            if (layer.TryGetProperty("deleteIds", out var deleteIds))
            {
                foreach (var deleteId in deleteIds.EnumerateArray().Select(id => id.GetInt64()))
                {
                    client.Remove(deleteId);
                }
            }

            var next = synced.GetProperty("serverGen").GetInt64();
            exceeded = synced.TryGetProperty("exceededTransferLimit", out var flag) && flag.GetBoolean();
            if (exceeded)
            {
                next.Should().BeGreaterThan(cursor, "a windowed download must move the cursor");
            }

            cursor = next;
            windows++;
        }

        windows.Should().BeGreaterThanOrEqualTo(3, "a 670-change backlog at no more than 250 changes per window needs at least three windows");
        client.Should().BeEquivalentTo(expected, "applying every window must converge on the stored rows");
    }

    private static void ApplyAdds(Dictionary<long, (string? Name, double X, double Y)> client, JsonElement features)
    {
        foreach (var row in features.EnumerateArray().Select(ReadFeature))
        {
            client.Should().NotContainKey(row.Id, "a row must be added exactly once across windows");
            client[row.Id] = (row.Name, row.X, row.Y);
        }
    }

    private static (long Id, string? Name, double X, double Y) ReadFeature(JsonElement feature)
        => (feature.GetProperty("attributes").GetProperty("objectid").GetInt64(),
            feature.GetProperty("attributes").GetProperty("name").GetString(),
            feature.GetProperty("geometry").GetProperty("x").GetDouble(),
            feature.GetProperty("geometry").GetProperty("y").GetDouble());

    private async Task<JsonElement> DownloadAsync(string replicaId, long replicaServerGen)
        => await PostJsonAsync("synchronizeReplica", new
        {
            replicaID = replicaId,
            syncDirection = "download",
            replicaServerGen,
            f = "json"
        });

    private async Task<JsonElement> PostJsonAsync(string operation, object payload)
    {
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{operation}", content);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(body);
        return document.RootElement.Clone();
    }

    private async Task InsertFeaturesAsync(long firstObjectId, int count, double longitude, double latitude)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (objectid, layer_id, geometry, attributes)
            SELECT @first + n,
                   0,
                   ST_SetSRID(ST_MakePoint(@longitude + n * 0.001, @latitude + n * 0.0005), 4326),
                   jsonb_build_object('name', format('window-%s', n))
            FROM generate_series(0, @count - 1) AS n;
            """;
        command.Parameters.AddWithValue("first", firstObjectId);
        command.Parameters.AddWithValue("count", count);
        command.Parameters.AddWithValue("longitude", longitude);
        command.Parameters.AddWithValue("latitude", latitude);
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExecuteSqlAsync(string sql, long firstObjectId)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("first", firstObjectId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Dictionary<long, (string? Name, double X, double Y)>> ReadRowsAsync(long fromObjectId, long toObjectId)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT objectid, attributes->>'name', ST_X(geometry), ST_Y(geometry)
            FROM features
            WHERE layer_id = 0 AND objectid BETWEEN @from AND @to
            """;
        command.Parameters.AddWithValue("from", fromObjectId);
        command.Parameters.AddWithValue("to", toObjectId);
        var rows = new Dictionary<long, (string? Name, double X, double Y)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows[reader.GetInt64(0)] = (reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3));
        }

        return rows;
    }
}
