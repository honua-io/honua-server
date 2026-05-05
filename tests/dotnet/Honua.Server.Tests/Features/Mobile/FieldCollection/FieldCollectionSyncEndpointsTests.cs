// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Tests.Features.Mobile.FieldCollection;

/// <summary>
/// Integration coverage for the FieldCollection mobile sync endpoints (#894).
/// Exercises the four endpoints (generation, sync-cursor, pull, push) consumed
/// by <c>honua-mobile</c> FieldCollection offline sync.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Features)]
public sealed class FieldCollectionSyncEndpointsTests : IAsyncLifetime
{
    private const string GenerationPath = "/api/v1/fieldcollection/generation";
    private const string SyncCursorPath = "/api/v1/fieldcollection/sync-cursor";
    private const string ChangesPath = "/api/v1/fieldcollection/changes";

    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/fieldcollection/generation")]
    public async Task Generation_ReturnsCurrentServerGeneration()
    {
        var response = await _client.GetAsync(GenerationPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.Should().BeTrue();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.TryGetProperty("serverGeneration", out var generation).Should().BeTrue();
        generation.GetInt64().Should().BeGreaterOrEqualTo(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/fieldcollection/sync-cursor")]
    public async Task SyncCursor_BeforeAnyPull_ReturnsZeroForCurrentClient()
    {
        var response = await _client.GetAsync(SyncCursorPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        json.RootElement.GetProperty("clientId").GetString().Should().NotBeNullOrWhiteSpace();
        json.RootElement.GetProperty("lastSyncGeneration").GetInt64().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/fieldcollection/changes")]
    public async Task Pull_SinceLatestGeneration_ReturnsEmptyList()
    {
        var response = await _client.GetAsync($"{ChangesPath}?sinceGeneration=999999999");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var changes = json.RootElement.GetProperty("changes");
        changes.ValueKind.Should().Be(JsonValueKind.Array);
        changes.GetArrayLength().Should().Be(0);
        json.RootElement.GetProperty("hasMore").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/fieldcollection/changes")]
    public async Task Push_NewInsert_ReturnsApplied()
    {
        var payload = new
        {
            changeId = Guid.NewGuid().ToString("N"),
            featureId = $"feat-{Guid.NewGuid():N}",
            layerId = 100,
            operation = "insert",
            timestamp = DateTimeOffset.UtcNow,
            feature = NewFeaturePayload(longitude: -122.4, latitude: 37.7),
        };

        var response = await _client.PostAsJsonAsync(ChangesPath, payload);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("changeId").GetString().Should().Be(payload.changeId);
        json.RootElement.GetProperty("outcome").GetString().Should().Be("applied");
        json.RootElement.GetProperty("serverGeneration").GetInt64().Should().BeGreaterThan(0);
        json.RootElement.GetProperty("version").GetInt64().Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/fieldcollection/changes")]
    public async Task Push_SameChangeId_ReturnsIdempotentOutcome()
    {
        var payload = new
        {
            changeId = Guid.NewGuid().ToString("N"),
            featureId = $"feat-{Guid.NewGuid():N}",
            layerId = 101,
            operation = "insert",
            timestamp = DateTimeOffset.UtcNow,
            feature = NewFeaturePayload(longitude: -73.9, latitude: 40.7),
        };

        var firstResponse = await _client.PostAsJsonAsync(ChangesPath, payload);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await firstResponse.Content.ReadAsStringAsync();
        using var firstJson = JsonDocument.Parse(firstBody);
        var firstGeneration = firstJson.RootElement.GetProperty("serverGeneration").GetInt64();
        var firstVersion = firstJson.RootElement.GetProperty("version").GetInt64();

        var secondResponse = await _client.PostAsJsonAsync(ChangesPath, payload);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = await secondResponse.Content.ReadAsStringAsync();
        using var secondJson = JsonDocument.Parse(secondBody);

        secondJson.RootElement.GetProperty("changeId").GetString().Should().Be(payload.changeId);
        secondJson.RootElement.GetProperty("outcome").GetString().Should().Be("applied");
        secondJson.RootElement.GetProperty("serverGeneration").GetInt64().Should().Be(firstGeneration);
        secondJson.RootElement.GetProperty("version").GetInt64().Should().Be(firstVersion);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/fieldcollection/changes")]
    public async Task Push_InvalidRequest_Returns400()
    {
        var response = await _client.PostAsJsonAsync(ChangesPath, new { changeId = "" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/fieldcollection/changes")]
    public async Task Push_UpdateMissingBaseVersion_ReturnsRejected()
    {
        var payload = new
        {
            changeId = Guid.NewGuid().ToString("N"),
            featureId = $"feat-{Guid.NewGuid():N}",
            layerId = 102,
            operation = "update",
            feature = NewFeaturePayload(longitude: -118.2, latitude: 34.0),
        };

        var response = await _client.PostAsJsonAsync(ChangesPath, payload);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("outcome").GetString().Should().Be("rejected");
        json.RootElement.GetProperty("rejectionReason").GetString().Should().Contain("baseVersion");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/fieldcollection/changes")]
    public async Task Push_ConflictingUpdate_ReturnsConflictWithServerFeature()
    {
        var featureId = $"feat-{Guid.NewGuid():N}";
        const int layerId = 103;

        var insertPayload = new
        {
            changeId = Guid.NewGuid().ToString("N"),
            featureId,
            layerId,
            operation = "insert",
            timestamp = DateTimeOffset.UtcNow,
            feature = NewFeaturePayload(longitude: 2.35, latitude: 48.85),
        };

        var insertResponse = await _client.PostAsJsonAsync(ChangesPath, insertPayload);
        insertResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var insertJson = JsonDocument.Parse(await insertResponse.Content.ReadAsStringAsync());
        var serverVersion = insertJson.RootElement.GetProperty("version").GetInt64();

        // First update lands cleanly using the correct baseVersion.
        var firstUpdate = new
        {
            changeId = Guid.NewGuid().ToString("N"),
            featureId,
            layerId,
            operation = "update",
            baseVersion = serverVersion,
            timestamp = DateTimeOffset.UtcNow,
            feature = NewFeaturePayload(longitude: 2.36, latitude: 48.86),
        };
        var firstUpdateResponse = await _client.PostAsJsonAsync(ChangesPath, firstUpdate);
        firstUpdateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var firstUpdateJson = JsonDocument.Parse(await firstUpdateResponse.Content.ReadAsStringAsync());
        firstUpdateJson.RootElement.GetProperty("outcome").GetString().Should().Be("applied");

        // Second update with the stale baseVersion must conflict.
        var conflictingUpdate = new
        {
            changeId = Guid.NewGuid().ToString("N"),
            featureId,
            layerId,
            operation = "update",
            baseVersion = serverVersion,
            timestamp = DateTimeOffset.UtcNow,
            feature = NewFeaturePayload(longitude: 2.37, latitude: 48.87),
        };
        var conflictResponse = await _client.PostAsJsonAsync(ChangesPath, conflictingUpdate);
        conflictResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var conflictJson = JsonDocument.Parse(await conflictResponse.Content.ReadAsStringAsync());
        conflictJson.RootElement.GetProperty("outcome").GetString().Should().Be("conflict");
        conflictJson.RootElement.GetProperty("conflictType").GetString().Should().Be("update-update");
        conflictJson.RootElement.GetProperty("serverVersion").GetInt64().Should().BeGreaterThan(serverVersion);
        conflictJson.RootElement.TryGetProperty("serverFeature", out var serverFeature).Should().BeTrue();
        serverFeature.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/fieldcollection/changes")]
    public async Task Pull_SinceGeneration_ReturnsOrderedChanges()
    {
        var generationBefore = await GetServerGenerationAsync();

        var changeIds = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var payload = new
            {
                changeId = Guid.NewGuid().ToString("N"),
                featureId = $"feat-{Guid.NewGuid():N}",
                layerId = 110,
                operation = "insert",
                timestamp = DateTimeOffset.UtcNow,
                feature = NewFeaturePayload(longitude: -100 + i, latitude: 30 + i),
            };
            changeIds.Add(payload.featureId);

            var pushResponse = await _client.PostAsJsonAsync(ChangesPath, payload);
            pushResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var pullResponse = await _client.GetAsync($"{ChangesPath}?sinceGeneration={generationBefore}&limit=200");
        pullResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await pullResponse.Content.ReadAsStringAsync());
        var changes = json.RootElement.GetProperty("changes");
        changes.ValueKind.Should().Be(JsonValueKind.Array);
        changes.GetArrayLength().Should().BeGreaterOrEqualTo(3);

        long previousGeneration = 0;
        foreach (var change in changes.EnumerateArray())
        {
            var generation = change.GetProperty("generation").GetInt64();
            generation.Should().BeGreaterThan(previousGeneration);
            previousGeneration = generation;
        }

        json.RootElement.GetProperty("nextCursor").GetInt64().Should().BeGreaterThan(generationBefore);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/fieldcollection/changes")]
    public async Task Pull_AdvancesPerClientCursor()
    {
        var generationBefore = await GetServerGenerationAsync();

        await _client.PostAsJsonAsync(ChangesPath, new
        {
            changeId = Guid.NewGuid().ToString("N"),
            featureId = $"feat-{Guid.NewGuid():N}",
            layerId = 120,
            operation = "insert",
            timestamp = DateTimeOffset.UtcNow,
            feature = NewFeaturePayload(longitude: 0.0, latitude: 0.0),
        });

        var pullResponse = await _client.GetAsync($"{ChangesPath}?sinceGeneration={generationBefore}");
        pullResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var pullJson = JsonDocument.Parse(await pullResponse.Content.ReadAsStringAsync());
        var nextCursor = pullJson.RootElement.GetProperty("nextCursor").GetInt64();
        nextCursor.Should().BeGreaterThan(generationBefore);

        var cursorResponse = await _client.GetAsync(SyncCursorPath);
        cursorResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var cursorJson = JsonDocument.Parse(await cursorResponse.Content.ReadAsStringAsync());
        cursorJson.RootElement.GetProperty("lastSyncGeneration").GetInt64().Should().BeGreaterOrEqualTo(nextCursor);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/fieldcollection/changes")]
    public async Task Pull_EmptyPage_AdvancesCursorToCurrentGeneration()
    {
        // Force a known watermark, then ask for changes after a generation that is
        // guaranteed to have nothing newer. The cursor must still advance so that
        // a client which has caught up does not re-pull the same window forever.
        var pullResponse = await _client.GetAsync($"{ChangesPath}?sinceGeneration=0&limit=200");
        pullResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var pullJson = JsonDocument.Parse(await pullResponse.Content.ReadAsStringAsync());
        var firstCursor = pullJson.RootElement.GetProperty("nextCursor").GetInt64();

        var emptyResponse = await _client.GetAsync($"{ChangesPath}?sinceGeneration={firstCursor}&limit=200");
        emptyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var emptyJson = JsonDocument.Parse(await emptyResponse.Content.ReadAsStringAsync());
        emptyJson.RootElement.GetProperty("changes").GetArrayLength().Should().Be(0);
        var emptyCursor = emptyJson.RootElement.GetProperty("nextCursor").GetInt64();
        emptyCursor.Should().BeGreaterOrEqualTo(firstCursor);

        var cursorResponse = await _client.GetAsync(SyncCursorPath);
        cursorResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var cursorJson = JsonDocument.Parse(await cursorResponse.Content.ReadAsStringAsync());
        cursorJson.RootElement.GetProperty("lastSyncGeneration").GetInt64().Should().BeGreaterOrEqualTo(emptyCursor);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/fieldcollection/sync-cursor")]
    public async Task SyncCursor_PartitionsByClientIdHeader()
    {
        const string headerName = "X-Honua-Client-Id";
        var clientA = $"device-a-{Guid.NewGuid():N}";
        var clientB = $"device-b-{Guid.NewGuid():N}";

        // Push something so a pull on clientA has at least the chance to advance
        // its cursor past zero.
        await _client.PostAsJsonAsync(ChangesPath, new
        {
            changeId = Guid.NewGuid().ToString("N"),
            featureId = $"feat-{Guid.NewGuid():N}",
            layerId = 130,
            operation = "insert",
            timestamp = DateTimeOffset.UtcNow,
            feature = NewFeaturePayload(longitude: 1.0, latitude: 1.0),
        });

        using var pullForA = new HttpRequestMessage(HttpMethod.Get, $"{ChangesPath}?sinceGeneration=0&limit=200");
        pullForA.Headers.Add(headerName, clientA);
        var pullAResponse = await _client.SendAsync(pullForA);
        pullAResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var pullAJson = JsonDocument.Parse(await pullAResponse.Content.ReadAsStringAsync());
        var advancedCursor = pullAJson.RootElement.GetProperty("nextCursor").GetInt64();
        advancedCursor.Should().BeGreaterOrEqualTo(0);

        using var cursorForA = new HttpRequestMessage(HttpMethod.Get, SyncCursorPath);
        cursorForA.Headers.Add(headerName, clientA);
        var cursorAResponse = await _client.SendAsync(cursorForA);
        cursorAResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var cursorAJson = JsonDocument.Parse(await cursorAResponse.Content.ReadAsStringAsync());
        cursorAJson.RootElement.GetProperty("clientId").GetString().Should().Be(clientA);
        var clientACursor = cursorAJson.RootElement.GetProperty("lastSyncGeneration").GetInt64();
        clientACursor.Should().BeGreaterOrEqualTo(advancedCursor);

        // ClientB never pulled, so its cursor is independent and zero.
        using var cursorForB = new HttpRequestMessage(HttpMethod.Get, SyncCursorPath);
        cursorForB.Headers.Add(headerName, clientB);
        var cursorBResponse = await _client.SendAsync(cursorForB);
        cursorBResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var cursorBJson = JsonDocument.Parse(await cursorBResponse.Content.ReadAsStringAsync());
        cursorBJson.RootElement.GetProperty("clientId").GetString().Should().Be(clientB);
        cursorBJson.RootElement.GetProperty("lastSyncGeneration").GetInt64().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/fieldcollection/changes")]
    public async Task Push_ConcurrentSameChangeId_BothReturnSameOutcome()
    {
        var payload = new
        {
            changeId = Guid.NewGuid().ToString("N"),
            featureId = $"feat-{Guid.NewGuid():N}",
            layerId = 140,
            operation = "insert",
            timestamp = DateTimeOffset.UtcNow,
            feature = NewFeaturePayload(longitude: -50.0, latitude: 5.0),
        };

        // Fire two pushes for the same changeId concurrently. The advisory lock
        // must serialize them and the loser must replay the stored idempotent
        // response — neither call may surface a 5xx from a unique violation.
        var firstTask = _client.PostAsJsonAsync(ChangesPath, payload);
        var secondTask = _client.PostAsJsonAsync(ChangesPath, payload);
        await Task.WhenAll(firstTask, secondTask);

        firstTask.Result.StatusCode.Should().Be(HttpStatusCode.OK);
        secondTask.Result.StatusCode.Should().Be(HttpStatusCode.OK);

        using var firstJson = JsonDocument.Parse(await firstTask.Result.Content.ReadAsStringAsync());
        using var secondJson = JsonDocument.Parse(await secondTask.Result.Content.ReadAsStringAsync());

        firstJson.RootElement.GetProperty("changeId").GetString().Should().Be(payload.changeId);
        secondJson.RootElement.GetProperty("changeId").GetString().Should().Be(payload.changeId);
        firstJson.RootElement.GetProperty("outcome").GetString().Should().Be("applied");
        secondJson.RootElement.GetProperty("outcome").GetString().Should().Be("applied");
        secondJson.RootElement.GetProperty("serverGeneration").GetInt64()
            .Should().Be(firstJson.RootElement.GetProperty("serverGeneration").GetInt64());
        secondJson.RootElement.GetProperty("version").GetInt64()
            .Should().Be(firstJson.RootElement.GetProperty("version").GetInt64());
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/fieldcollection/changes")]
    public async Task Push_ConcurrentInsertsForSameAbsentFeature_OneAppliesOneConflicts()
    {
        // Two distinct change_ids both target the same (feature_id, layer_id) for
        // an absent feature. The change_id advisory lock does not serialize them
        // (different keys), and SELECT ... FOR UPDATE locks nothing when the row
        // is absent — so without the feature-identity advisory lock both would
        // resolve as Applied and the second upsert would silently overwrite the
        // first. With the fix, the loser re-reads the freshly committed row and
        // resolves as Conflict. Exactly one applied + one conflict is the
        // expected outcome.
        var featureId = $"feat-{Guid.NewGuid():N}";
        var layerId = 170;
        var payloadA = new
        {
            changeId = Guid.NewGuid().ToString("N"),
            featureId,
            layerId,
            operation = "insert",
            timestamp = DateTimeOffset.UtcNow,
            feature = NewFeaturePayload(longitude: -33.0, latitude: 11.0),
        };
        var payloadB = new
        {
            changeId = Guid.NewGuid().ToString("N"),
            featureId,
            layerId,
            operation = "insert",
            timestamp = DateTimeOffset.UtcNow,
            feature = NewFeaturePayload(longitude: -34.0, latitude: 12.0),
        };

        var taskA = _client.PostAsJsonAsync(ChangesPath, payloadA);
        var taskB = _client.PostAsJsonAsync(ChangesPath, payloadB);
        await Task.WhenAll(taskA, taskB);

        taskA.Result.StatusCode.Should().Be(HttpStatusCode.OK);
        taskB.Result.StatusCode.Should().Be(HttpStatusCode.OK);

        using var jsonA = JsonDocument.Parse(await taskA.Result.Content.ReadAsStringAsync());
        using var jsonB = JsonDocument.Parse(await taskB.Result.Content.ReadAsStringAsync());

        var outcomeA = jsonA.RootElement.GetProperty("outcome").GetString();
        var outcomeB = jsonB.RootElement.GetProperty("outcome").GetString();

        // Exactly one push must apply and the other must conflict on the
        // existing row. Without the feature-identity advisory lock both would
        // resolve as Applied and the second upsert would silently overwrite.
        (outcomeA == "applied" ^ outcomeB == "applied").Should().BeTrue(
            "exactly one of the two concurrent inserts must apply; got A={0}, B={1}", outcomeA, outcomeB);
        (outcomeA == "conflict" ^ outcomeB == "conflict").Should().BeTrue(
            "exactly one of the two concurrent inserts must conflict; got A={0}, B={1}", outcomeA, outcomeB);

        var conflictJson = outcomeA == "conflict" ? jsonA : jsonB;
        conflictJson.RootElement.GetProperty("conflictType").GetString().Should().Be("update-update");
        conflictJson.RootElement.TryGetProperty("serverVersion", out var serverVersion).Should().BeTrue();
        serverVersion.GetInt64().Should().Be(1L);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/fieldcollection/changes")]
    public async Task Push_DeleteWithNonNullFeature_Returns400()
    {
        // The contract requires 'feature' to be null for delete operations.
        // Forwarding the payload to the store would silently drop it; instead
        // the endpoint must reject the wire shape with a 400 problem-details.
        var payload = new
        {
            changeId = Guid.NewGuid().ToString("N"),
            featureId = $"feat-{Guid.NewGuid():N}",
            layerId = 150,
            operation = "delete",
            baseVersion = 1L,
            timestamp = DateTimeOffset.UtcNow,
            feature = NewFeaturePayload(longitude: 0.0, latitude: 0.0),
        };

        var response = await _client.PostAsJsonAsync(ChangesPath, payload);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/fieldcollection/generation")]
    public async Task Generation_DoesNotReflectUncommittedSequenceAllocations()
    {
        // The watermark must be the committed MAX(generation) of FieldCollection
        // changes, not the shared sync_generation sequence's last_value. A
        // writer that has called nextval but not yet committed leaves the
        // sequence ahead of any committed row; reading last_value would let
        // an empty pull advance past that pending write and never observe it.
        // We simulate the bug condition by bumping the sequence directly and
        // confirming /generation and the empty-pull cursor both stay at the
        // committed FieldCollection max.
        var beforeResponse = await _client.GetAsync(GenerationPath);
        beforeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var beforeJson = JsonDocument.Parse(await beforeResponse.Content.ReadAsStringAsync());
        var committedMax = beforeJson.RootElement.GetProperty("serverGeneration").GetInt64();

        using var scope = _fixture.Services.CreateScope();
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using (var npgsqlConnection = await dataSource.OpenConnectionAsync())
        {
            await using var bumpCmd = new NpgsqlCommand(
                "SELECT setval('honua.sync_generation', last_value + 100, true) FROM honua.sync_generation",
                npgsqlConnection);
            _ = await bumpCmd.ExecuteScalarAsync();
        }

        var afterResponse = await _client.GetAsync(GenerationPath);
        afterResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var afterJson = JsonDocument.Parse(await afterResponse.Content.ReadAsStringAsync());
        afterJson.RootElement.GetProperty("serverGeneration").GetInt64().Should().Be(committedMax);

        var emptyPullResponse = await _client.GetAsync($"{ChangesPath}?sinceGeneration={committedMax}&limit=200");
        emptyPullResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var emptyPullJson = JsonDocument.Parse(await emptyPullResponse.Content.ReadAsStringAsync());
        emptyPullJson.RootElement.GetProperty("changes").GetArrayLength().Should().Be(0);
        emptyPullJson.RootElement.GetProperty("nextCursor").GetInt64().Should().Be(committedMax);
        emptyPullJson.RootElement.GetProperty("serverGeneration").GetInt64().Should().Be(committedMax);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/fieldcollection/changes")]
    public async Task Push_LoserAfterAdvisoryLockReplaysWinnerOutcomeWithoutDuplicateKeyError()
    {
        // Regression for the RepeatableRead idempotency-replay race. A holding
        // transaction acquires the advisory lock and stages an idempotency row
        // for changeId X without committing. The HTTP loser opens its own
        // transaction, fast-path read sees nothing, then blocks on the advisory
        // lock. The holder commits, the loser proceeds, and under the fix
        // (ReadCommitted) the post-lock idempotency read sees the freshly
        // committed row and replays it as 200 OK. Under the old RepeatableRead
        // shape the loser's snapshot was taken before the holder committed and
        // the post-lock read returned count=0, which would surface as a unique
        // violation 5xx.
        const int FieldCollectionPushLockNamespace = 0x0894_FC5C;
        var changeId = Guid.NewGuid().ToString("N");
        var featureId = $"feat-{Guid.NewGuid():N}";
        var layerId = 160;

        // The store persists FieldCollectionPushResult via the source-generated
        // JSON context with JsonSerializerDefaults.General (PascalCase property
        // names, numeric enum values). Match that on-disk shape so the loser's
        // post-lock TryReadIdempotencyResponseAsync deserializes it cleanly.
        var stagedResponse = $$"""
            {
                "ChangeId": "{{changeId}}",
                "Outcome": 1,
                "ServerGeneration": 999999,
                "Version": 1,
                "ConflictType": 0,
                "ServerFeaturePayloadJson": null,
                "ServerVersion": null,
                "RejectionReason": null
            }
            """;

        using var scope = _fixture.Services.CreateScope();
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        // Holder: acquire the advisory lock and stage the idempotency row, but
        // do not commit yet. The loser HTTP push will block on the same lock.
        await using var holderConnection = await dataSource.OpenConnectionAsync();
        await using var holderTransaction = await holderConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        await using (var lockCmd = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@namespace, hashtext(@change_id))",
            holderConnection,
            holderTransaction))
        {
            lockCmd.Parameters.AddWithValue("namespace", NpgsqlDbType.Integer, FieldCollectionPushLockNamespace);
            lockCmd.Parameters.AddWithValue("change_id", NpgsqlDbType.Text, changeId);
            _ = await lockCmd.ExecuteScalarAsync();
        }

        await using (var insertCmd = new NpgsqlCommand(
            """
            INSERT INTO honua.fieldcollection_pushed_changes (
                change_id, feature_id, layer_id, operation, outcome, response_payload, pushed_at)
            VALUES (
                @change_id, @feature_id, @layer_id, 1, 1, @payload::jsonb, now())
            """,
            holderConnection,
            holderTransaction))
        {
            insertCmd.Parameters.AddWithValue("change_id", NpgsqlDbType.Text, changeId);
            insertCmd.Parameters.AddWithValue("feature_id", NpgsqlDbType.Text, featureId);
            insertCmd.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, layerId);
            insertCmd.Parameters.AddWithValue("payload", NpgsqlDbType.Text, stagedResponse);
            _ = await insertCmd.ExecuteNonQueryAsync();
        }

        // Kick off the loser push — it must enter its transaction and start
        // waiting on the advisory lock before the holder commits.
        var loserPayload = new
        {
            changeId,
            featureId,
            layerId,
            operation = "insert",
            timestamp = DateTimeOffset.UtcNow,
            feature = NewFeaturePayload(longitude: 12.5, latitude: 6.0),
        };
        var loserTask = _client.PostAsJsonAsync(ChangesPath, loserPayload);

        // Give the loser a moment to reach the BeginTransaction → advisory lock
        // wait. 750ms is comfortably more than a request takes to set up.
        await Task.Delay(750);
        loserTask.IsCompleted.Should().BeFalse(
            "the loser must be parked on the advisory lock until the holder commits");

        await holderTransaction.CommitAsync();

        var loserResponse = await loserTask;
        var loserBody = await loserResponse.Content.ReadAsStringAsync();
        loserResponse.StatusCode.Should().Be(HttpStatusCode.OK, "loser body: {0}", loserBody);
        using var loserJson = JsonDocument.Parse(loserBody);
        loserJson.RootElement.GetProperty("changeId").GetString().Should().Be(changeId);
        loserJson.RootElement.GetProperty("outcome").GetString().Should().Be("applied");
        loserJson.RootElement.GetProperty("serverGeneration").GetInt64().Should().Be(999999);
        loserJson.RootElement.GetProperty("version").GetInt64().Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/fieldcollection/changes")]
    public async Task Pull_WithInvalidLimit_Returns400()
    {
        var response = await _client.GetAsync($"{ChangesPath}?limit=0");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<long> GetServerGenerationAsync()
    {
        var response = await _client.GetAsync(GenerationPath);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("serverGeneration").GetInt64();
    }

    private static object NewFeaturePayload(double longitude, double latitude) => new
    {
        type = "Feature",
        geometry = new
        {
            type = "Point",
            coordinates = new[] { longitude, latitude },
        },
        properties = new
        {
            name = "Integration test feature",
            collectedAt = DateTimeOffset.UtcNow,
        },
    };
}

/// <summary>
/// Verifies that the four FieldCollection sync endpoints reject anonymous traffic
/// when API-key authentication is enforced.
/// </summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Features)]
public sealed class FieldCollectionSyncAuthorizationTests : IAsyncLifetime
{
    private const string AdminPassword = "fieldcollection-auth-test-key";
    private static readonly double[] OriginCoordinates = [0.0, 0.0];
    private readonly WebAppFixture _fixture;
    private HttpClient _unauthenticatedClient = null!;

    public FieldCollectionSyncAuthorizationTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _unauthenticatedClient = _fixture.CreateClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/fieldcollection/generation")]
    public async Task Generation_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/fieldcollection/generation");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/fieldcollection/sync-cursor")]
    public async Task SyncCursor_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/fieldcollection/sync-cursor");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/fieldcollection/changes")]
    public async Task PullChanges_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/fieldcollection/changes");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/fieldcollection/changes")]
    public async Task PushChange_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PostAsJsonAsync("/api/v1/fieldcollection/changes", new
        {
            changeId = Guid.NewGuid().ToString("N"),
            featureId = "feat-unauth",
            layerId = 1,
            operation = "insert",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/fieldcollection/changes")]
    public async Task PushChange_WithApiKey_AcceptsRequest()
    {
        using var authedClient = _fixture.CreateClient(client =>
        {
            client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
        });

        var response = await authedClient.PostAsJsonAsync("/api/v1/fieldcollection/changes", new
        {
            changeId = Guid.NewGuid().ToString("N"),
            featureId = $"feat-{Guid.NewGuid():N}",
            layerId = 200,
            operation = "insert",
            timestamp = DateTimeOffset.UtcNow,
            feature = new
            {
                type = "Feature",
                geometry = new
                {
                    type = "Point",
                    coordinates = OriginCoordinates,
                },
                properties = new { name = "auth-test" },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
