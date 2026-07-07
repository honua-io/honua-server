// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.ControlPlane;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// End-to-end coverage for the workflow-transition seam (#2562): deploy lifecycle transitions raised
/// through <see cref="IWorkflowOperationTransitionListener"/> surface as <c>Release</c> events on the
/// Operate timeline and are filterable via the existing observability events endpoint.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class DeployReleaseTimelineTests : IAsyncLifetime
{
    private readonly ReleaseTimelineBuffer _buffer = new();
    private readonly InMemoryWorkflowOperationStore _inner = new();
    private readonly TimelineDeployBackend _backend = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public DeployReleaseTimelineTests()
    {
        var store = new TransitionObservingWorkflowOperationStore(
            _inner,
            new IWorkflowOperationTransitionListener[] { new ReleaseTimelineTransitionListener(_buffer) },
            NullLogger<TransitionObservingWorkflowOperationStore>.Instance);

        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDatabaseMigrationRunner>();
                services.AddSingleton<IDatabaseMigrationRunner>(new StubDatabaseMigrationRunner());
                services.RemoveAll<IDeployTargetRegistry>();
                services.AddSingleton<IDeployTargetRegistry>(new TimelineTargetRegistry());
                services.RemoveAll<IWorkflowOperationStore>();
                services.AddSingleton<IWorkflowOperationStore>(store);
                services.RemoveAll<IWorkflowOperationReconciler>();
                services.AddSingleton<IWorkflowOperationReconciler>(new StubReconciler());
                services.AddSingleton<IDeployBackend>(_backend);
                // Ensure the operate feed reads the same buffer the transition listener writes to.
                services.RemoveAll<ReleaseTimelineBuffer>();
                services.AddSingleton(_buffer);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/events")]
    public async Task DeployLifecycle_RaisesReleaseTimelineEvents_FilterableByReleaseId()
    {
        // Operation 1: created -> submitted -> promoted -> rolled back.
        var releaseOne = $"sha256:timeline-{Guid.NewGuid():N}";
        var operationOne = await CreateAndDriveAsync(releaseOne, rollbackToManualIntervention: false);

        // Operation 2: created -> submitted -> manual intervention (rollback with no backend recovery).
        var releaseTwo = $"sha256:manual-{Guid.NewGuid():N}";
        var operationTwo = await CreateAndDriveAsync(releaseTwo, rollbackToManualIntervention: true);

        // All release events are visible on the timeline when filtering by kind.
        var allReleaseEvents = await GetReleaseEventsAsync("kind=Release&pageSize=200");
        var titlesForOne = allReleaseEvents
            .Where(e => e.GetProperty("operationId").GetString() == operationOne)
            .Select(e => e.GetProperty("title").GetString())
            .ToList();
        titlesForOne.Should().Contain(t => t!.StartsWith("Deploy operation created"));
        titlesForOne.Should().Contain(t => t!.StartsWith("Deploy submitted"));
        titlesForOne.Should().Contain(t => t!.StartsWith("Deploy promoted"));
        titlesForOne.Should().Contain(t => t!.StartsWith("Deploy rolled back"));

        var titlesForTwo = allReleaseEvents
            .Where(e => e.GetProperty("operationId").GetString() == operationTwo)
            .Select(e => e.GetProperty("title").GetString())
            .ToList();
        titlesForTwo.Should().Contain(t => t!.StartsWith("Deploy needs manual intervention"));

        // The ReleaseId filter now returns data and scopes to a single release.
        var scoped = await GetReleaseEventsAsync($"releaseId={Uri.EscapeDataString(releaseOne)}&pageSize=200");
        scoped.Should().NotBeEmpty();
        scoped.Should().OnlyContain(e => e.GetProperty("releaseId").GetString() == releaseOne);
        scoped.Should().OnlyContain(e => e.GetProperty("operationId").GetString() == operationOne);
    }

    private async Task<string> CreateAndDriveAsync(string desiredRevision, bool rollbackToManualIntervention)
    {
        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/deploy/operations", new
        {
            targetId = "timeline-target",
            desiredRevision,
            submitImmediately = false
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        using var createDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var operationId = createDocument.RootElement.GetProperty("operationId").GetString()!;

        var submitResponse = await _client.PostAsJsonAsync(
            $"/api/v1/admin/deploy/operations/{operationId}/submit", new { reason = "Approved" });
        submitResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        if (rollbackToManualIntervention)
        {
            _backend.NextRollbackStatus = WorkflowOperationStatus.ManualInterventionRequired;
        }
        else
        {
            var promoteResponse = await _client.PostAsJsonAsync(
                $"/api/v1/admin/deploy/operations/{operationId}/promote", new { reason = "Cutover" });
            promoteResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            _backend.NextRollbackStatus = WorkflowOperationStatus.RolledBack;
        }

        var rollbackResponse = await _client.PostAsJsonAsync(
            $"/api/v1/admin/deploy/operations/{operationId}/rollback", new { reason = "Verification failed" });
        rollbackResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        return operationId;
    }

    private async Task<List<JsonElement>> GetReleaseEventsAsync(string queryString)
    {
        var response = await _client.GetAsync($"/api/v1/admin/observability/events?{queryString}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items")
            .EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() == "release")
            .Select(item => item.Clone())
            .ToList();
    }

    private sealed class TimelineDeployBackend : IDeployBackend
    {
        public WorkflowOperationStatus NextRollbackStatus { get; set; } = WorkflowOperationStatus.RolledBack;

        public string BackendName => "timeline-backend";

        public DeployTargetKind TargetKind => DeployTargetKind.Kubernetes;

        public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployBackendCapabilities { SupportsRollback = true, SupportsProgressPolling = true });

        public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployPlan { IsReadyToSubmit = true });

        public Task<DeploySubmissionResult> StartAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeploySubmissionResult { Status = WorkflowOperationStatus.Submitted, Message = "Submitted" });

        public Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation { Status = WorkflowOperationStatus.Reconciling });

        public Task<DeployObservation> PromoteAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.Succeeded,
                ObservedRevision = operation.Deploy?.DesiredRevision,
                Message = "Promoted"
            });

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = NextRollbackStatus,
                Message = NextRollbackStatus == WorkflowOperationStatus.ManualInterventionRequired
                    ? "Rollback requires manual intervention"
                    : "Rolled back"
            });
    }

    private sealed class TimelineTargetRegistry : IDeployTargetRegistry
    {
        private static readonly DeployTargetDefinition Target = new()
        {
            TargetId = "timeline-target",
            TargetKind = DeployTargetKind.Kubernetes,
            Backend = "timeline-backend",
            Environment = "production",
            TargetName = "honua-server",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        };

        public Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeployTargetDefinition>>([Target]);

        public Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromResult(targetId == Target.TargetId ? Target : null);
    }

    private sealed class StubReconciler : IWorkflowOperationReconciler
    {
        public Task ReconcileWorkflowOperationAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubDatabaseMigrationRunner : IDatabaseMigrationRunner
    {
        public Task<DatabaseMigrationPlan> PlanMigrationsAsync(string connectionString, Assembly migrationsAssembly, CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseMigrationPlan.Succeeded());

        public Task<DatabaseMigrationResult> RunMigrationsAsync(string connectionString, Assembly migrationsAssembly, CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseMigrationResult.Succeeded());
    }

    private sealed class InMemoryWorkflowOperationStore : IWorkflowOperationStore
    {
        private readonly Dictionary<string, WorkflowOperationRecord> _operations = new(StringComparer.Ordinal);

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCreateAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            if (_operations.ContainsKey(operation.OperationId))
            {
                return Task.FromResult(false);
            }

            _operations[operation.OperationId] = operation;
            return Task.FromResult(true);
        }

        public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryGetValue(operationId, out var operation) ? operation : null);

        public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkflowOperationRecord?>(null);

        public Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(
                _operations.Values.Where(op => !kind.HasValue || op.Kind == kind.Value).ToArray());
    }
}
