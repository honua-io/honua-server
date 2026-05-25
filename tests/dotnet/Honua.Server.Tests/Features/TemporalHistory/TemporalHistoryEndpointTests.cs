// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.TemporalHistory.Abstractions;
using Honua.Core.Features.TemporalHistory.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.TemporalHistory;

/// <summary>
/// Integration tests for the temporal data-history API surface.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Query)]
public sealed class TemporalHistoryEndpointTests : IAsyncLifetime
{
    private const int TemporalLayerId = 0;
    private const int RestrictedLayerId = 1;
    private const string Cursor = "ts:2024-01-01T00:00:00.0000000Z";
    private static readonly string NormalizedCursor =
        TemporalCursor.AtTimestamp(DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture)).ToString();

    private readonly RecordingExecutionJobStore _jobStore = new();
    private readonly RecordingJobQueue _jobQueue = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public TemporalHistoryEndpointTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<ILayerCatalog>();
                services.RemoveAll<ITemporalHistorySource>();
                services.RemoveAll<IExecutionJobStore>();
                services.RemoveAll<IJobQueue>();
                services.AddSingleton<ILayerCatalog, StubTemporalLayerCatalog>();
                services.AddSingleton<ITemporalHistorySource, StubTemporalHistorySource>();
                services.AddSingleton<IExecutionJobStore>(_jobStore);
                services.AddSingleton<IJobQueue>(_jobQueue);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/layers/{layerId}/history/capabilities")]
    [Endpoint("GET /api/v1/layers/{layerId}/history/checkpoints")]
    [Endpoint("GET /api/v1/layers/{layerId}/history/items")]
    [Endpoint("GET /api/v1/layers/{layerId}/history/diff")]
    [Endpoint("GET /api/v1/layers/{layerId}/history/items/{featureId}/timeline")]
    [Endpoint("GET /api/v1/layers/{layerId}/history/rollback-plan")]
    [Endpoint("POST /api/v1/layers/{layerId}/history/rollback")]
    public async Task TemporalHistoryEndpoints_ExerciseContractsAndQueueRollbackJob()
    {
        var capabilities = await _client.GetAsync($"/api/v1/layers/{TemporalLayerId}/history/capabilities");
        capabilities.StatusCode.Should().Be(HttpStatusCode.OK);
        (await capabilities.Content.ReadAsStringAsync()).Should().Contain("\"supportsAsOf\":true");

        var checkpoints = await _client.GetAsync($"/api/v1/layers/{TemporalLayerId}/history/checkpoints");
        checkpoints.StatusCode.Should().Be(HttpStatusCode.OK);
        (await checkpoints.Content.ReadAsStringAsync()).Should().Contain("\"checkpoints\"");

        var items = await _client.GetAsync($"/api/v1/layers/{TemporalLayerId}/history/items?at={Uri.EscapeDataString(Cursor)}");
        items.StatusCode.Should().Be(HttpStatusCode.OK);
        (await items.Content.ReadAsStringAsync()).Should().Contain("\"items\"");

        var diff = await _client.GetAsync(
            $"/api/v1/layers/{TemporalLayerId}/history/diff?from={Uri.EscapeDataString(Cursor)}&to={Uri.EscapeDataString(Cursor)}");
        diff.StatusCode.Should().Be(HttpStatusCode.OK);
        (await diff.Content.ReadAsStringAsync()).Should().Contain("\"attributeChanged\":1");

        var timeline = await _client.GetAsync($"/api/v1/layers/{TemporalLayerId}/history/items/feature-1/timeline");
        timeline.StatusCode.Should().Be(HttpStatusCode.OK);
        (await timeline.Content.ReadAsStringAsync()).Should().Contain("\"revisions\"");

        var rollbackPlan = await _client.GetAsync(
            $"/api/v1/layers/{TemporalLayerId}/history/rollback-plan?to={Uri.EscapeDataString(Cursor)}");
        rollbackPlan.StatusCode.Should().Be(HttpStatusCode.OK);
        (await rollbackPlan.Content.ReadAsStringAsync()).Should().Contain("\"requiresJob\":true");

        using var content = new StringContent(
            $$"""{"to":"{{Cursor}}","approved":true,"reason":"integration test"}""",
            Encoding.UTF8,
            "application/json");
        var rollback = await _client.PostAsync($"/api/v1/layers/{TemporalLayerId}/history/rollback", content);
        rollback.StatusCode.Should().Be(HttpStatusCode.Accepted);

        _jobStore.Created.Should().ContainSingle();
        var job = _jobStore.Created.Single();
        job.Spec.Kind.Should().Be(ExecutionJobKind.TemporalRollback);
        job.Spec.Parameters["honua.temporal.rollback.layer_id"].Should().Be(
            TemporalLayerId.ToString(CultureInfo.InvariantCulture));
        job.Spec.Parameters["honua.temporal.rollback.to_cursor"].Should().Be(NormalizedCursor);
        _jobQueue.Enqueued.Should().ContainSingle(job.OperationId);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/layers/{layerId}/history/items")]
    [Endpoint("GET /api/v1/layers/{layerId}/history/diff")]
    public async Task TemporalHistoryEndpoints_EnforceSeparateHistoryAndDiffPermissions()
    {
        var history = await _client.GetAsync($"/api/v1/layers/{RestrictedLayerId}/history/items?at={Uri.EscapeDataString(Cursor)}");
        history.StatusCode.Should().Be(HttpStatusCode.OK);

        var diff = await _client.GetAsync(
            $"/api/v1/layers/{RestrictedLayerId}/history/diff?from={Uri.EscapeDataString(Cursor)}&to={Uri.EscapeDataString(Cursor)}");
        diff.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private sealed class StubTemporalLayerCatalog : ILayerCatalog
    {
        private readonly LayerDefinition[] _layers =
        [
            BuildLayer(TemporalLayerId, AdminTemporalPolicy()),
            BuildLayer(RestrictedLayerId, new TemporalAccessPolicy
            {
                HistoryReadRoles = ["admin"],
                DiffReadRoles = ["diff-reader"],
                TimelineReadRoles = ["timeline-reader"],
                RollbackPlanRoles = ["rollback-planner"],
                RollbackExecuteRoles = ["rollback-executor"]
            })
        ];

        public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(_layers.FirstOrDefault(layer => layer.Id == layerId));

        public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_layers);

        public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult<ServiceDefinition?>(null);

        public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<ServiceDefinition>());

        public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(_layers.Any(layer => layer.Id == layerId));

        public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<Relationship?> GetRelationshipAsync(
            int layerId,
            int relationshipId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<Relationship?>(null);

        public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<Relationship>());

        private static LayerDefinition BuildLayer(int layerId, TemporalAccessPolicy temporalPolicy)
            => LayerDefinition.CreateBasic(layerId, $"temporal-{layerId}", GeometryType.Point) with
            {
                Metadata = new CatalogMetadata
                {
                    AccessPolicy = new AccessPolicy
                    {
                        AllowedRoles = ["admin"],
                        AllowedWriteRoles = ["admin"]
                    },
                    TemporalSource = new TemporalSourceConfig
                    {
                        SourceKind = TemporalSourceKind.AuditLog,
                        AllowRollback = true,
                        AccessPolicy = temporalPolicy
                    }
                }
            };

        private static TemporalAccessPolicy AdminTemporalPolicy()
            => new()
            {
                HistoryReadRoles = ["admin"],
                DiffReadRoles = ["admin"],
                TimelineReadRoles = ["admin"],
                RollbackPlanRoles = ["admin"],
                RollbackExecuteRoles = ["admin"]
            };
    }

    private sealed class StubTemporalHistorySource : ITemporalHistorySource
    {
        public Task<TemporalSourceCapabilityInfo?> GetCapabilitiesAsync(
            LayerDefinition layer,
            CancellationToken cancellationToken = default)
            => Task.FromResult<TemporalSourceCapabilityInfo?>(new TemporalSourceCapabilityInfo
            {
                LayerId = layer.Id,
                SupportsAsOf = true,
                SupportsHistory = true,
                SupportsDiff = true,
                SupportsTimeline = true,
                SupportsRollbackPlan = true,
                SupportsRollbackExecution = true,
                SupportsGeometryHistory = true,
                SupportsAttribution = true,
                SourceKind = TemporalSourceKind.AuditLog,
                AttributionFields = ["actor", "source_ref", "correlation_id"],
                SchemaEvolution = SchemaEvolutionPolicy.Fixed,
                GeometrySrid = 4326
            });

        public Task<DateTimeOffset?> ResolveCursorAsync(
            LayerDefinition layer,
            TemporalCursor cursor,
            CancellationToken cancellationToken = default)
            => Task.FromResult(cursor.Timestamp);

        public Task<IReadOnlyList<TemporalCheckpoint>> ListCheckpointsAsync(
            LayerDefinition layer,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TemporalCheckpoint>>(
            [
                new TemporalCheckpoint
                {
                    Cursor = Cursor,
                    Label = "fixture",
                    Timestamp = DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture),
                    Kind = "timestamp"
                }
            ]);

        public Task<TemporalSnapshot> QueryAsOfAsync(
            LayerDefinition layer,
            TemporalCursor at,
            TemporalPageRequest page,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TemporalSnapshot
            {
                LayerId = layer.Id,
                At = at.ToString(),
                ResolvedAt = at.Timestamp ?? DateTimeOffset.UnixEpoch,
                GeneratedAt = DateTimeOffset.Parse("2024-01-02T00:00:00Z", CultureInfo.InvariantCulture),
                Srid = 4326,
                Items =
                [
                    new TemporalFeature
                    {
                        Id = "feature-1",
                        Attributes = new Dictionary<string, JsonElement>
                        {
                            ["name"] = JsonSerializer.SerializeToElement("alpha")
                        }
                    }
                ]
            });

        public Task<TemporalDiff> DiffAsync(
            LayerDefinition layer,
            TemporalCursor fromCursor,
            TemporalCursor toCursor,
            TemporalPageRequest page,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TemporalDiff
            {
                LayerId = layer.Id,
                From = fromCursor.ToString(),
                To = toCursor.ToString(),
                Summary = new TemporalDiffSummary { AttributeChanged = 1 },
                Items =
                [
                    new TemporalFeatureChange
                    {
                        FeatureId = "feature-1",
                        ChangeKind = TemporalChangeKind.AttributeChanged,
                        FieldChanges =
                        [
                            new TemporalFieldChange
                            {
                                Field = "name",
                                Before = JsonSerializer.SerializeToElement("alpha"),
                                After = JsonSerializer.SerializeToElement("beta")
                            }
                        ],
                        Attribution = new TemporalAttribution
                        {
                            Actor = "editor",
                            ChangedAt = DateTimeOffset.Parse("2024-01-01T01:00:00Z", CultureInfo.InvariantCulture)
                        }
                    }
                ]
            });

        public Task<TemporalTimeline> GetTimelineAsync(
            LayerDefinition layer,
            string featureId,
            TemporalPageRequest page,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TemporalTimeline
            {
                LayerId = layer.Id,
                FeatureId = featureId,
                Revisions =
                [
                    new TemporalRevision
                    {
                        Cursor = Cursor,
                        Operation = "UPDATE",
                        FieldChanges =
                        [
                            new TemporalFieldChange
                            {
                                Field = "name",
                                Before = JsonSerializer.SerializeToElement("alpha"),
                                After = JsonSerializer.SerializeToElement("beta")
                            }
                        ],
                        Attribution = new TemporalAttribution { Actor = "editor" }
                    }
                ]
            });

        public Task<TemporalRollbackPlan> PlanRollbackAsync(
            LayerDefinition layer,
            TemporalCursor toCursor,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TemporalRollbackPlan
            {
                LayerId = layer.Id,
                To = toCursor.ToString(),
                Mode = TemporalRollbackMode.JobRequired,
                AffectedCount = 1,
                RequiresApproval = true,
                RequiresJob = true
            });

        public Task<TemporalRollbackResult> ExecuteRollbackAsync(
            LayerDefinition layer,
            TemporalCursor toCursor,
            TemporalRollbackContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TemporalRollbackResult
            {
                LayerId = layer.Id,
                JobId = context.JobId,
                AppliedCount = 1,
                Checkpoint = Cursor
            });
    }

    private sealed class RecordingExecutionJobStore : IExecutionJobStore
    {
        private readonly Dictionary<string, ExecutionJobRecord> _jobs = new(StringComparer.Ordinal);

        public List<ExecutionJobRecord> Created { get; } = [];

        public Task<bool> TryAcquireLeaseAsync(
            string operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(
            string operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCreateAsync(
            ExecutionJobRecord job,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            Created.Add(job);
            _jobs[job.OperationId] = job;
            return Task.FromResult(true);
        }

        public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_jobs.TryGetValue(operationId, out var job) ? job : null);

        public Task SetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _jobs[job.OperationId] = job;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(
            ExecutionJobRecord job,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            _jobs[job.OperationId] = job;
            return Task.FromResult(true);
        }

        public Task<ExecutionJobPage> QueryAsync(ExecutionJobQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionJobPage { Items = _jobs.Values.ToArray() });

        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(
            ExecutionJobKind? kind = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionJobRecord>>(_jobs.Values.ToArray());
    }

    private sealed class RecordingJobQueue : IJobQueue
    {
        public List<string> Enqueued { get; } = [];

        public Task EnqueueAsync(
            string operationId,
            OperationPriority priority = OperationPriority.Normal,
            CancellationToken cancellationToken = default)
        {
            Enqueued.Add(operationId);
            return Task.CompletedTask;
        }

        public Task<string?> TryClaimAsync(
            string workerId,
            IReadOnlySet<ExecutionJobKind>? acceptedKinds = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task RequeueAsync(
            string operationId,
            OperationPriority priority = OperationPriority.Normal,
            TimeSpan? visibleAfter = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<long> GetQueueDepthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult((long)Enqueued.Count);
    }
}
