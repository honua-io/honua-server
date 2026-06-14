// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Temporal.Abstractions;
using Honua.Core.Features.Temporal.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Temporal;

/// <summary>
/// Integration tests for slices 2-5 of the temporal data history API (honua-server#1166): diff, feature
/// timeline, attribution surfacing, and governed rollback planning/execution. The Metadata v2 graph,
/// change tracker, and temporal history store are overridden with deterministic fakes so the real
/// <c>TemporalHistoryService</c>, the admin endpoints, the distinct authorization policies, and AOT JSON
/// serialization are exercised end-to-end. Rollback execution uses a fake runner so the job-handle
/// contract is verified without depending on the durable job runner.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class TemporalHistorySliceEndpointsTests : IAsyncLifetime
{
    private const string ServiceId = "svc-temporal";
    private const int TemporalLayerId = 10;
    private const int NonTemporalLayerId = 11;

    private readonly FakeChangeTracker _changeTracker = new();
    private readonly FakeTemporalHistoryStore _store = new();
    private readonly FakeRollbackRunner _rollbackRunner = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public TemporalHistorySliceEndpointsTests()
    {
        _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
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

                services.RemoveAll<ITemporalHistoryStore>();
                services.AddScoped<ITemporalHistoryStore>(_ => _store);

                services.RemoveAll<ITemporalRollbackRunner>();
                services.AddScoped<ITemporalRollbackRunner>(_ => _rollbackRunner);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/temporal/services/{serviceId}/layers/{layerId}/diff")]
    public async Task Diff_BetweenCheckpoints_ReturnsClassifiedChanges()
    {
        _changeTracker.CurrentGeneration = 20;
        var changedAt = DateTimeOffset.Parse("2026-05-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        _store.WindowRows =
        [
            new TemporalChangeRecord(3, 100, TemporalChangeKind.Insert, changedAt,
                new TemporalAttribution("alice", TemporalAttributionSource.EditSession, "applyEdits", "sess-1")),
            new TemporalChangeRecord(5, 200, TemporalChangeKind.Update, changedAt, null),
            new TemporalChangeRecord(7, 300, TemporalChangeKind.Delete, changedAt, null),
        ];
        _store.ChangedCount = 3;

        var response = await _client.GetAsync(
            $"/api/v1/temporal/services/{ServiceId}/layers/{TemporalLayerId}/diff?from=2&to=20");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = (await ReadJsonAsync(response)).RootElement;

        root.GetProperty("from").GetProperty("generation").GetInt64().Should().Be(2);
        root.GetProperty("to").GetProperty("generation").GetInt64().Should().Be(20);

        var summary = root.GetProperty("summary");
        summary.GetProperty("added").GetInt32().Should().Be(1);
        summary.GetProperty("removed").GetInt32().Should().Be(1);
        summary.GetProperty("attributeChanged").GetInt32().Should().Be(1);
        summary.GetProperty("total").GetInt32().Should().Be(3);

        var changes = root.GetProperty("changes");
        changes.GetArrayLength().Should().Be(3);
        changes[0].GetProperty("objectId").GetInt64().Should().Be(100);
        changes[0].GetProperty("primaryClass").GetString().Should().Be("Added");
        // Attribution (slice 4) is surfaced on the diff change.
        changes[0].GetProperty("attribution").GetProperty("actor").GetString().Should().Be("alice");
        changes[0].GetProperty("attribution").GetProperty("source").GetString().Should().Be("EditSession");
        changes[2].GetProperty("primaryClass").GetString().Should().Be("Removed");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/temporal/services/{serviceId}/layers/{layerId}/diff")]
    public async Task Diff_MissingFrom_ReturnsBadRequest()
    {
        _changeTracker.CurrentGeneration = 5;
        var response = await _client.GetAsync(
            $"/api/v1/temporal/services/{ServiceId}/layers/{TemporalLayerId}/diff");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/temporal/services/{serviceId}/layers/{layerId}/diff")]
    public async Task Diff_ForNonTemporalLayer_ReturnsConflict()
    {
        var response = await _client.GetAsync(
            $"/api/v1/temporal/services/{ServiceId}/layers/{NonTemporalLayerId}/diff?from=0");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/temporal/services/{serviceId}/layers/{layerId}/features/{featureId}/timeline")]
    public async Task Timeline_ForFeature_ReturnsOrderedRevisions()
    {
        _changeTracker.CurrentGeneration = 9;
        var changedAt = DateTimeOffset.Parse("2026-05-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        _store.FeatureRevisions =
        [
            new TemporalChangeRecord(2, 100, TemporalChangeKind.Insert, changedAt,
                new TemporalAttribution("bob", TemporalAttributionSource.Import, "import", "job-7")),
            new TemporalChangeRecord(6, 100, TemporalChangeKind.Update, changedAt, null),
        ];

        var response = await _client.GetAsync(
            $"/api/v1/temporal/services/{ServiceId}/layers/{TemporalLayerId}/features/100/timeline");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = (await ReadJsonAsync(response)).RootElement;
        root.GetProperty("objectId").GetInt64().Should().Be(100);
        var revisions = root.GetProperty("revisions");
        revisions.GetArrayLength().Should().Be(2);
        revisions[0].GetProperty("generation").GetInt64().Should().Be(2);
        revisions[0].GetProperty("operation").GetString().Should().Be("Insert");
        revisions[0].GetProperty("attribution").GetProperty("actor").GetString().Should().Be("bob");
        revisions[1].GetProperty("operation").GetString().Should().Be("Update");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/temporal/services/{serviceId}/layers/{layerId}/rollback/plan")]
    public async Task RollbackPlan_ForTemporalLayer_ReportsState()
    {
        _changeTracker.CurrentGeneration = 20;
        _store.ChangedCount = 4;

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/temporal/services/{ServiceId}/layers/{TemporalLayerId}/rollback/plan",
            new { checkpoint = new { kind = "generation", generation = 5 } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = (await ReadJsonAsync(response)).RootElement;
        root.GetProperty("state").GetString().Should().Be("Supported");
        root.GetProperty("affectedFeatureCount").GetInt32().Should().Be(4);
        root.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
        root.GetProperty("targetCheckpoint").GetProperty("generation").GetInt64().Should().Be(5);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/temporal/services/{serviceId}/layers/{layerId}/rollback")]
    public async Task Rollback_WithoutApproval_ReturnsConflict()
    {
        _changeTracker.CurrentGeneration = 20;
        _store.ChangedCount = 4;

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/temporal/services/{ServiceId}/layers/{TemporalLayerId}/rollback",
            new { checkpoint = new { kind = "generation", generation = 5 }, approved = false });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/temporal/services/{serviceId}/layers/{layerId}/rollback")]
    public async Task Rollback_Approved_SubmitsJobThroughRunner()
    {
        _changeTracker.CurrentGeneration = 20;
        _store.ChangedCount = 4;

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/temporal/services/{ServiceId}/layers/{TemporalLayerId}/rollback",
            new { checkpoint = new { kind = "generation", generation = 5 }, approved = true, reason = "bad import" });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var root = (await ReadJsonAsync(response)).RootElement;
        root.GetProperty("jobId").GetString().Should().Be("fake-job-1");
        root.GetProperty("status").GetString().Should().Be("Queued");
        _rollbackRunner.SubmittedTargetGeneration.Should().Be(5);
        _rollbackRunner.SubmittedReason.Should().Be("bad import");
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

        public Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CurrentGeneration);

        public Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
            long sinceGeneration,
            int[] layerIds,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FeatureChange>>(Array.Empty<FeatureChange>());
    }

    private sealed class FakeTemporalHistoryStore : ITemporalHistoryStore
    {
        public IReadOnlyList<TemporalChangeRecord> WindowRows { get; set; } = Array.Empty<TemporalChangeRecord>();

        public IReadOnlyList<TemporalChangeRecord> FeatureRevisions { get; set; } = Array.Empty<TemporalChangeRecord>();

        public int ChangedCount { get; set; }

        public Task<IReadOnlyList<TemporalChangeRecord>> GetFeatureRevisionsAsync(
            int storageLayerId, long objectId, long afterGeneration, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(FeatureRevisions);

        public Task<IReadOnlyList<TemporalChangeRecord>> GetChangesInWindowAsync(
            int storageLayerId, long fromGeneration, long toGeneration, long afterObjectId, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(WindowRows);

        public Task<int> CountChangedFeaturesAsync(
            int storageLayerId, long fromGeneration, long toGeneration, CancellationToken cancellationToken = default)
            => Task.FromResult(ChangedCount);

        public Task<long?> ResolveCheckpointGenerationAsync(
            int storageLayerId, TemporalCheckpointKind kind, string value, long upperBoundGeneration, CancellationToken cancellationToken = default)
            => Task.FromResult<long?>(null);
    }

    private sealed class FakeRollbackRunner : ITemporalRollbackRunner
    {
        public long SubmittedTargetGeneration { get; private set; }

        public string? SubmittedReason { get; private set; }

        public Task<TemporalRollbackJobHandle> SubmitRollbackAsync(
            string serviceId,
            int layerId,
            int storageLayerId,
            ResolvedTemporalCheckpoint target,
            string? reason,
            CancellationToken cancellationToken = default)
        {
            SubmittedTargetGeneration = target.Generation;
            SubmittedReason = reason;
            return Task.FromResult(new TemporalRollbackJobHandle(
                JobId: "fake-job-1",
                ServiceId: serviceId,
                LayerId: layerId,
                TargetCheckpoint: target,
                Status: "Queued"));
        }
    }
}
