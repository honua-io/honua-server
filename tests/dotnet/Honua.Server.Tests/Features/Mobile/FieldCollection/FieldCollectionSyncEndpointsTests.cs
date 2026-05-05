// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

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
