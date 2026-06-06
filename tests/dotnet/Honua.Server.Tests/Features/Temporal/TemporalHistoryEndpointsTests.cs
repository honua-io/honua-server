// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Temporal;

/// <summary>
/// Integration tests for slice 1 of the temporal data history API (honua-server#1166):
/// capability discovery and as-of read. The Metadata v2 graph is overridden with a layer that
/// declares a temporal column (history-capable) and a layer that does not (unsupported), and the
/// change tracker is replaced with a deterministic fake so as-of reads are reproducible. This
/// exercises the real <c>TemporalHistoryService</c>, the admin endpoint, the temporal-history
/// authorization policy, and AOT JSON serialization end-to-end.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class TemporalHistoryEndpointsTests : IAsyncLifetime
{
    private const string ServiceId = "svc-temporal";
    private const int TemporalLayerId = 10;
    private const int NonTemporalLayerId = 11;

    private readonly FakeChangeTracker _changeTracker = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public TemporalHistoryEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.RemoveAll<IMetadataV2GraphStore>();
                services.AddSingleton(_ => new TestMetadataV2GraphProvider(BuildTemporalGraph()));
                services.AddSingleton<IMetadataV2GraphProvider>(sp =>
                    sp.GetRequiredService<TestMetadataV2GraphProvider>());
                services.AddSingleton<IMetadataV2GraphStore>(sp =>
                    sp.GetRequiredService<TestMetadataV2GraphProvider>());

                services.RemoveAll<IChangeTracker>();
                services.AddScoped<IChangeTracker>(_ => _changeTracker);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/temporal/services/{serviceId}/layers/{layerId}/capabilities")]
    public async Task Capability_ForTemporalLayer_ReportsHistorySupported()
    {
        _changeTracker.CurrentGeneration = 42;

        var response = await _client.GetAsync(
            $"/api/v1/temporal/services/{ServiceId}/layers/{TemporalLayerId}/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        var root = json.RootElement;

        root.GetProperty("supportsHistory").GetBoolean().Should().BeTrue();
        root.GetProperty("supportsAsOf").GetBoolean().Should().BeTrue();
        root.GetProperty("temporalColumn").GetString().Should().Be("valid_from");
        root.GetProperty("cursorKind").GetString().Should().Be("Generation");
        root.GetProperty("currentGeneration").GetInt64().Should().Be(42);

        // Slices 2-5 are implemented: a history-capable layer now advertises diff/timeline/attribution/
        // rollback support through the (formerly deferred) capability flags.
        var deferred = root.GetProperty("deferred");
        deferred.GetProperty("supportsDiff").GetBoolean().Should().BeTrue();
        deferred.GetProperty("supportsTimeline").GetBoolean().Should().BeTrue();
        deferred.GetProperty("supportsAttribution").GetBoolean().Should().BeTrue();
        deferred.GetProperty("supportsRollback").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/temporal/services/{serviceId}/layers/{layerId}/capabilities")]
    public async Task Capability_ForNonTemporalLayer_ReportsHistoryUnsupported()
    {
        var response = await _client.GetAsync(
            $"/api/v1/temporal/services/{ServiceId}/layers/{NonTemporalLayerId}/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        json.RootElement.GetProperty("supportsHistory").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("supportsAsOf").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/temporal/services/{serviceId}/layers/{layerId}/capabilities")]
    public async Task Capability_ForUnknownLayer_ReturnsNotFound()
    {
        var response = await _client.GetAsync(
            $"/api/v1/temporal/services/{ServiceId}/layers/9999/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/temporal/services/{serviceId}/layers/{layerId}/as-of")]
    public async Task AsOf_ForTemporalLayer_ReturnsCollapsedChanges()
    {
        var changedAt = DateTimeOffset.Parse("2026-05-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        _changeTracker.CurrentGeneration = 5;
        _changeTracker.ChangesSince = sinceGeneration => sinceGeneration == 2
            ?
            [
                new FeatureChange
                {
                    ChangeId = 1, Generation = 3, LayerId = TemporalLayerId, ObjectId = 100,
                    Operation = FeatureChangeOperation.Insert, ChangedAt = changedAt
                },
                new FeatureChange
                {
                    ChangeId = 2, Generation = 4, LayerId = TemporalLayerId, ObjectId = 200,
                    Operation = FeatureChangeOperation.Delete, ChangedAt = changedAt
                }
            ]
            : [];

        var response = await _client.GetAsync(
            $"/api/v1/temporal/services/{ServiceId}/layers/{TemporalLayerId}/as-of?generation=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        var root = json.RootElement;

        root.GetProperty("resolvedGeneration").GetInt64().Should().Be(2);
        root.GetProperty("currentGeneration").GetInt64().Should().Be(5);
        root.GetProperty("requestedCursorKind").GetString().Should().Be("Generation");

        var features = root.GetProperty("features");
        features.GetArrayLength().Should().Be(2);

        var insert = features[0];
        insert.GetProperty("objectId").GetInt64().Should().Be(100);
        insert.GetProperty("operation").GetString().Should().Be("Insert");

        var delete = features[1];
        delete.GetProperty("objectId").GetInt64().Should().Be(200);
        delete.GetProperty("operation").GetString().Should().Be("Delete");
        // Deleted features expose no attribute snapshot in slice 1.
        delete.TryGetProperty("attributes", out var deletedAttrs).Should().BeFalse();
        _ = deletedAttrs;
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/temporal/services/{serviceId}/layers/{layerId}/as-of")]
    public async Task AsOf_WhenUnchanged_ReturnsEmptyDeterministically()
    {
        _changeTracker.CurrentGeneration = 7;
        _changeTracker.ChangesSince = _ => [];

        var response = await _client.GetAsync(
            $"/api/v1/temporal/services/{ServiceId}/layers/{TemporalLayerId}/as-of?generation=7");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        json.RootElement.GetProperty("features").GetArrayLength().Should().Be(0);
        json.RootElement.GetProperty("resolvedGeneration").GetInt64().Should().Be(7);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/temporal/services/{serviceId}/layers/{layerId}/as-of")]
    public async Task AsOf_ForNonTemporalLayer_ReturnsConflict()
    {
        var response = await _client.GetAsync(
            $"/api/v1/temporal/services/{ServiceId}/layers/{NonTemporalLayerId}/as-of?generation=0");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/temporal/services/{serviceId}/layers/{layerId}/as-of")]
    public async Task AsOf_WithBothCursors_ReturnsBadRequest()
    {
        var response = await _client.GetAsync(
            $"/api/v1/temporal/services/{ServiceId}/layers/{TemporalLayerId}/as-of?generation=1&timestamp=2026-05-01T00:00:00Z");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/temporal/services/{serviceId}/layers/{layerId}/as-of")]
    public async Task AsOf_WithTimestampCursor_ReturnsBadRequest()
    {
        // Slice 1 keys on the change-tracker generation only; there is no timestamp -> generation
        // index, so a timestamp cursor must be rejected with a 400 rather than silently treated as
        // generation 0 (which would return the full change feed even for a future timestamp).
        // Make the full feed non-empty so a regression to generation-0 behavior would be observable.
        _changeTracker.CurrentGeneration = 5;
        _changeTracker.ChangesSince = sinceGeneration => sinceGeneration == 0
            ?
            [
                new FeatureChange
                {
                    ChangeId = 1, Generation = 1, LayerId = TemporalLayerId, ObjectId = 100,
                    Operation = FeatureChangeOperation.Insert,
                    ChangedAt = DateTimeOffset.Parse("2026-05-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)
                }
            ]
            : [];

        var response = await _client.GetAsync(
            $"/api/v1/temporal/services/{ServiceId}/layers/{TemporalLayerId}/as-of?timestamp=2099-01-01T00:00:00Z");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content);
    }

    private static MetadataV2Graph BuildTemporalGraph()
    {
        var builder = new TestMetadataV2GraphBuilder()
            .AddService(ServiceId, ServiceId, accessPolicy: new Honua.Core.Features.Security.Domain.AccessPolicy { AllowAnonymous = true });

        builder
            .AddResource("res-temporal", "temporal-layer")
            .AddStorageBinding(
                "binding-temporal",
                "res-temporal",
                "temporal_features",
                storageLayerId: TemporalLayerId,
                options: new Dictionary<string, JsonElement>
                {
                    ["temporalColumn"] = JsonSerializer.SerializeToElement("valid_from")
                })
            .AddPublication(
                id: "pub-temporal",
                serviceId: ServiceId,
                resourceId: "res-temporal",
                layerIndex: TemporalLayerId,
                serviceLocalId: TemporalLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        builder
            .AddResource("res-plain", "plain-layer")
            .AddStorageBinding(
                "binding-plain",
                "res-plain",
                "plain_features",
                storageLayerId: NonTemporalLayerId)
            .AddPublication(
                id: "pub-plain",
                serviceId: ServiceId,
                resourceId: "res-plain",
                layerIndex: NonTemporalLayerId,
                serviceLocalId: NonTemporalLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return builder.Build();
    }

    private sealed class FakeChangeTracker : IChangeTracker
    {
        public long CurrentGeneration { get; set; }

        public Func<long, IReadOnlyList<FeatureChange>> ChangesSince { get; set; } = _ => [];

        public Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CurrentGeneration);

        public Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
            long sinceGeneration,
            int[] layerIds,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ChangesSince(sinceGeneration));
    }
}
