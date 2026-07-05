// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// API-surface coverage for the Demo C coordinated platform-upgrade release endpoints: create, read,
/// gate approve, and rollback. The conductor stage machine itself is covered by
/// <see cref="Infrastructure.ControlPlane.CoordinatedReleaseReconcilerTests"/>. The composed container
/// and metadata step executors are faked here so the endpoint surface is exercised without standing up
/// live deploy backends.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Metadata)]
public sealed class CoordinatedReleaseControlEndpointsTests : IAsyncLifetime
{
    private readonly InMemoryWorkflowOperationStore _workflowStore = new();
    private readonly FakeContainerStep _container = new();
    private readonly FakeMetadataStep _metadata = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public CoordinatedReleaseControlEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IWorkflowOperationStore>();
                services.AddSingleton<IWorkflowOperationStore>(_workflowStore);

                services.RemoveAll<CoordinatedReleaseControlService>();
                services.AddSingleton<CoordinatedReleaseControlService>();
                services.RemoveAll<ICoordinatedContainerStepExecutor>();
                services.AddSingleton<ICoordinatedContainerStepExecutor>(_container);
                services.RemoveAll<ICoordinatedMetadataStepExecutor>();
                services.AddSingleton<ICoordinatedMetadataStepExecutor>(_metadata);
                services.RemoveAll<ICoordinatedReleaseReconciler>();
                services.AddSingleton<ICoordinatedReleaseReconciler, CoordinatedReleaseReconciler>();
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/coordinated-releases/operations")]
    public async Task Create_WithCoordinatedRequest_Returns201WithThreeArtifacts()
    {
        var response = await CreateAsync($"pkg-{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("Submitted");
        root.GetProperty("currentStep").GetString().Should().Be("Preflight");

        var artifacts = root.GetProperty("artifacts").EnumerateArray().ToList();
        artifacts.Should().HaveCount(3);
        artifacts.Select(a => a.GetProperty("kind").GetString())
            .Should().Contain(["ContainerImage", "DatabaseSchema", "Metadata"]);

        // Ordered ledger with the two approval gates present.
        var steps = root.GetProperty("steps").EnumerateArray().ToList();
        steps.Single(s => s.GetProperty("step").GetString() == "ContainerRollout")
            .GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
        steps.Single(s => s.GetProperty("step").GetString() == "MetadataAndSchema")
            .GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/coordinated-releases/operations")]
    public async Task Create_WithMissingFields_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/metadata/coordinated-releases/operations", new
        {
            packageId = "pkg-1",
            targetEnvironment = "production"
            // container + resource fields intentionally omitted
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/coordinated-releases/{packageId}/operation")]
    public async Task Get_AfterCreate_ReturnsOperationAndAdvancesToContainerGate()
    {
        var packageId = $"pkg-{Guid.NewGuid():N}";
        (await CreateAsync(packageId)).StatusCode.Should().Be(HttpStatusCode.Created);

        // Repeated GETs drive the conductor one step per read; with no approval it parks at the gate.
        JsonElement root = default;
        for (var i = 0; i < 6; i++)
        {
            var response = await _client.GetAsync($"/api/v1/admin/metadata/coordinated-releases/{packageId}/operation");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            root = document.RootElement.Clone();
        }

        root.GetProperty("status").GetString().Should().Be("AwaitingApproval");
        root.GetProperty("currentStep").GetString().Should().Be("ContainerRollout");
        _container.StartCalls.Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/coordinated-releases/{packageId}/operation")]
    public async Task Get_ForUnknownPackage_Returns404()
    {
        var response = await _client.GetAsync("/api/v1/admin/metadata/coordinated-releases/does-not-exist/operation");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approve/{gate}")]
    public async Task ApproveGate_ThenDriveToCompletion_RunsContainerThenMetadata()
    {
        var packageId = $"pkg-{Guid.NewGuid():N}";
        var created = await CreateAsync(packageId);
        var operationId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("operationId").GetString()!;

        // Drive + approve each gate as it appears until terminal.
        JsonElement root = default;
        for (var i = 0; i < 40; i++)
        {
            using var response = await _client.GetAsync($"/api/v1/admin/metadata/coordinated-releases/{packageId}/operation");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            root = document.RootElement.Clone();
            var status = root.GetProperty("status").GetString();

            if (status == "AwaitingApproval")
            {
                var step = root.GetProperty("currentStep").GetString();
                var gate = step == "ContainerRollout" ? "container" : "data";
                var approve = await _client.PostAsJsonAsync(
                    $"/api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approve/{gate}",
                    new { reason = "approved" });
                approve.StatusCode.Should().Be(HttpStatusCode.OK);
                continue;
            }

            if (status is "Succeeded" or "Failed" or "RolledBack" or "ManualInterventionRequired")
            {
                break;
            }
        }

        root.GetProperty("status").GetString().Should().Be("Succeeded");
        _container.StartCalls.Should().Be(1);
        _metadata.StartCalls.Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/coordinated-releases/operations/{operationId}/rollback")]
    public async Task Rollback_AfterApprovingGates_RequestsCoordinatedRollback()
    {
        var packageId = $"pkg-{Guid.NewGuid():N}";
        var created = await CreateAsync(packageId);
        var operationId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("operationId").GetString()!;

        // Approve the container gate and let the container step succeed so a reversible step has run.
        for (var i = 0; i < 8; i++)
        {
            using var response = await _client.GetAsync($"/api/v1/admin/metadata/coordinated-releases/{packageId}/operation");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            if (root.GetProperty("status").GetString() == "AwaitingApproval" &&
                root.GetProperty("currentStep").GetString() == "ContainerRollout")
            {
                await _client.PostAsJsonAsync(
                    $"/api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approve/container",
                    new { reason = "approved" });
                break;
            }
        }

        // Operator explicitly requests the coordinated rollback through the rollback endpoint.
        var rollback = await _client.PostAsJsonAsync(
            $"/api/v1/admin/metadata/coordinated-releases/operations/{operationId}/rollback",
            new { reason = "operator-initiated rollback" });

        rollback.StatusCode.Should().Be(HttpStatusCode.OK);
        using var rollbackDoc = JsonDocument.Parse(await rollback.Content.ReadAsStringAsync());
        rollbackDoc.RootElement.GetProperty("status").GetString().Should().Be("RollbackRequested");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/coordinated-releases/operations/{operationId}/rollback")]
    public async Task Rollback_FaultInMetadataStep_UnwindsBothReversibleSteps()
    {
        _metadata.ObserveOutcome = CoordinatedStepOutcome.Failed;

        var packageId = $"pkg-{Guid.NewGuid():N}";
        var created = await CreateAsync(packageId);
        var operationId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("operationId").GetString()!;

        JsonElement root = default;
        for (var i = 0; i < 40; i++)
        {
            using var response = await _client.GetAsync($"/api/v1/admin/metadata/coordinated-releases/{packageId}/operation");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            root = document.RootElement.Clone();
            var status = root.GetProperty("status").GetString();

            if (status == "AwaitingApproval")
            {
                var step = root.GetProperty("currentStep").GetString();
                var gate = step == "ContainerRollout" ? "container" : "data";
                await _client.PostAsJsonAsync(
                    $"/api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approve/{gate}",
                    new { reason = "approved" });
                continue;
            }

            if (status is "Succeeded" or "Failed" or "RolledBack" or "ManualInterventionRequired")
            {
                break;
            }
        }

        // The metadata-step fault triggered ONE coordinated rollback that unwound both reversible steps.
        root.GetProperty("status").GetString().Should().Be("RolledBack");
        _container.RollbackCalls.Should().Be(1);
        _metadata.RollbackCalls.Should().Be(1);
    }

    private Task<HttpResponseMessage> CreateAsync(string packageId)
        => _client.PostAsJsonAsync("/api/v1/admin/metadata/coordinated-releases/operations", new
        {
            packageId,
            targetEnvironment = "production",
            containerTargetId = "prod-ecs",
            desiredImage = "honua/server:v21",
            currentImage = "honua/server:v20",
            resourceSemanticId = "parcels",
            newFieldName = "owner_email",
            newFieldType = "String",
            reason = "Demo C coordinated upgrade"
        });

    // ---- fakes -----------------------------------------------------------

    private sealed class FakeContainerStep : ICoordinatedContainerStepExecutor
    {
        public int StartCalls { get; private set; }
        public int RollbackCalls { get; private set; }
        public CoordinatedStepOutcome ObserveOutcome { get; set; } = CoordinatedStepOutcome.Succeeded;

        public Task<CoordinatedStepResult> StartAsync(CoordinatedReleaseContext context, WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return Task.FromResult(new CoordinatedStepResult { Outcome = CoordinatedStepOutcome.Pending, ChildOperationId = "deploy-child" });
        }

        public Task<CoordinatedStepResult> ObserveAsync(string childOperationId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CoordinatedStepResult { Outcome = ObserveOutcome, ChildOperationId = childOperationId });

        public Task RollbackAsync(string childOperationId, CancellationToken cancellationToken = default)
        {
            RollbackCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMetadataStep : ICoordinatedMetadataStepExecutor
    {
        public int StartCalls { get; private set; }
        public int RollbackCalls { get; private set; }
        public CoordinatedStepOutcome ObserveOutcome { get; set; } = CoordinatedStepOutcome.Succeeded;

        public Task<CoordinatedStepResult> StartAsync(CoordinatedReleaseContext context, WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return Task.FromResult(new CoordinatedStepResult { Outcome = CoordinatedStepOutcome.Pending, ChildOperationId = "metadata-child" });
        }

        public Task<CoordinatedStepResult> ObserveAsync(string childOperationId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CoordinatedStepResult { Outcome = ObserveOutcome, ChildOperationId = childOperationId });

        public Task RollbackAsync(string childOperationId, CancellationToken cancellationToken = default)
        {
            RollbackCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryWorkflowOperationStore : IWorkflowOperationStore
    {
        private readonly ConcurrentDictionary<string, WorkflowOperationRecord> _operations = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _leases = new(StringComparer.Ordinal);

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(_leases.TryAdd(operationId, ownerId));

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(_leases.TryGetValue(operationId, out var currentOwner) && currentOwner == ownerId);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
        {
            _leases.TryRemove(new KeyValuePair<string, string>(operationId, ownerId));
            return Task.CompletedTask;
        }

        public Task<bool> TryCreateAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryAdd(operation.OperationId, operation));

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
        {
            var operations = _operations.Values
                .Where(operation => !kind.HasValue || operation.Kind == kind.Value)
                .ToArray();
            return Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(operations);
        }
    }
}
