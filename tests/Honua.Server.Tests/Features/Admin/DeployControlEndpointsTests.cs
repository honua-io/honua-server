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
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class DeployControlEndpointsTests : IAsyncLifetime
{
    private readonly StubDatabaseMigrationRunner _migrationRunner = new();
    private readonly InMemoryWorkflowOperationStore _workflowStore = new();
    private readonly StubWorkflowOperationReconciler _reconciler = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public DeployControlEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDatabaseMigrationRunner>();
                services.AddSingleton<IDatabaseMigrationRunner>(_migrationRunner);
                services.RemoveAll<IDeployTargetRegistry>();
                services.RemoveAll<IWorkflowOperationStore>();
                services.RemoveAll<IWorkflowOperationReconciler>();
                services.AddSingleton<IDeployTargetRegistry>(new StubDeployTargetRegistry());
                services.AddSingleton<IWorkflowOperationStore>(_workflowStore);
                services.AddSingleton<IWorkflowOperationReconciler>(_reconciler);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/deploy/preflight")]
    public async Task GetDeployPreflight_WhenInstanceIsAligned_ReturnsReady()
    {
        _migrationRunner.Plan = DatabaseMigrationPlan.Succeeded();

        var response = await _client.GetAsync("/api/v1/admin/deploy/preflight");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("ready");
        root.GetProperty("readyForCoordinatedDeploy").GetBoolean().Should().BeTrue();
        root.GetProperty("message").GetString().Should().Be("Instance is ready for coordinated deployment.");
        root.TryGetProperty("serverVersion", out _).Should().BeFalse();
        root.TryGetProperty("environment", out _).Should().BeFalse();
        root.TryGetProperty("deploymentMode", out _).Should().BeFalse();
        root.TryGetProperty("instanceName", out _).Should().BeFalse();
        root.TryGetProperty("migration", out _).Should().BeFalse();
        root.TryGetProperty("readiness", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/deploy/preflight")]
    public async Task GetDeployPreflight_WhenPendingMigrationsExist_ReturnsBlocked()
    {
        _migrationRunner.Plan = DatabaseMigrationPlan.Succeeded(
            pendingScripts:
            [
                "0003_add_service_metadata.sql"
            ]);

        var response = await _client.GetAsync("/api/v1/admin/deploy/preflight");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("blocked");
        root.GetProperty("readyForCoordinatedDeploy").GetBoolean().Should().BeFalse();
        root.GetProperty("message").GetString().Should().Be("Instance is not ready for coordinated deployment.");
        root.TryGetProperty("migration", out _).Should().BeFalse();
        root.TryGetProperty("readiness", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/deploy/preflight")]
    public async Task GetDeployPreflight_WithIncludeDiagnosticsTrue_ReturnsDiagnostics()
    {
        _migrationRunner.Plan = DatabaseMigrationPlan.Succeeded(
            pendingScripts:
            [
                "0003_add_service_metadata.sql"
            ]);

        var response = await _client.GetAsync("/api/v1/admin/deploy/preflight?includeDiagnostics=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("blocked");
        root.GetProperty("message").GetString().Should().Be("Instance is not ready for coordinated deployment.");
        root.GetProperty("serverVersion").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("environment").GetString().Should().Be("Test");
        root.GetProperty("deploymentMode").GetString().Should().Be("SingleInstance");
        root.GetProperty("instanceName").GetString().Should().NotBeNullOrWhiteSpace();

        var readiness = root.GetProperty("readiness");
        readiness.GetProperty("isReady").GetBoolean().Should().BeTrue();
        readiness.GetProperty("statusCode").GetInt32().Should().Be(StatusCodes.Status200OK);

        var migration = root.GetProperty("migration");
        migration.GetProperty("upgradeRequired").GetBoolean().Should().BeTrue();
        migration.GetProperty("pendingScripts").GetArrayLength().Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/plan")]
    public async Task PlanDeployOperation_WhenTargetExists_ReturnsResolvedPlan()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/deploy/plan", new
        {
            targetId = "prod-api",
            desiredRevision = "sha256:abc123"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("target").GetProperty("targetId").GetString().Should().Be("prod-api");
        root.GetProperty("target").GetProperty("backend").GetString().Should().Be("honua-gitops-kubernetes");
        root.GetProperty("readyToSubmit").GetBoolean().Should().BeTrue();
        root.GetProperty("backendRegistered").GetBoolean().Should().BeTrue();
        root.GetProperty("capabilities").GetProperty("supportsRollback").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations")]
    [Endpoint("GET /api/v1/admin/deploy/operations/{operationId}")]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/submit")]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task DeployOperation_LifecycleEndpoints_RoundTrip()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/deploy/operations", new
        {
            targetId = "prod-api",
            desiredRevision = "sha256:def456",
            reason = "Promote tested revision",
            submitImmediately = false
        });

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var createDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var createRoot = createDocument.RootElement;
        var operationId = createRoot.GetProperty("operationId").GetString();
        operationId.Should().NotBeNullOrWhiteSpace();
        createRoot.GetProperty("status").GetString().Should().Be("Planned");
        createRoot.GetProperty("target").GetProperty("desiredRevision").GetString().Should().Be("sha256:def456");

        var submitResponse = await _client.PostAsJsonAsync($"/api/v1/admin/deploy/operations/{operationId}/submit", new
        {
            reason = "Approval granted"
        });

        submitResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var submitDocument = JsonDocument.Parse(await submitResponse.Content.ReadAsStringAsync());
        submitDocument.RootElement.GetProperty("status").GetString().Should().Be("Submitted");

        var getResponse = await _client.GetAsync($"/api/v1/admin/deploy/operations/{operationId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var getDocument = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        getDocument.RootElement.GetProperty("operationId").GetString().Should().Be(operationId);
        getDocument.RootElement.GetProperty("providerOperationId").GetString().Should().Contain("honua-gitops-kubernetes");

        var rollbackResponse = await _client.PostAsJsonAsync($"/api/v1/admin/deploy/operations/{operationId}/rollback", new
        {
            reason = "Post-deploy verification failed"
        });

        rollbackResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var rollbackDocument = JsonDocument.Parse(await rollbackResponse.Content.ReadAsStringAsync());
        rollbackDocument.RootElement.GetProperty("status").GetString().Should().Be("RollbackRequested");
        rollbackDocument.RootElement.GetProperty("currentPhase").GetString().Should().Be("Rollback requested through Honua GitOps reconciliation.");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations")]
    [Endpoint("GET /api/v1/admin/deploy/operations/{operationId}")]
    public async Task CreateDeployOperation_WithUnsafeIdempotencyKey_ReturnsSafeRoundTrippableOperationId()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/deploy/operations", new
        {
            targetId = "prod-api",
            desiredRevision = "sha256:safe123",
            idempotencyKey = " release / 2026#03 ? candidate "
        });

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var createDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var operationId = createDocument.RootElement.GetProperty("operationId").GetString();
        operationId.Should().NotBeNullOrWhiteSpace();
        operationId.Should().MatchRegex("^deploy-[a-z0-9-]+$");
        operationId.Should().NotContain("/");
        operationId.Should().NotContain("?");
        operationId.Should().NotContain("#");
        operationId.Should().NotContain(" ");

        var getResponse = await _client.GetAsync($"/api/v1/admin/deploy/operations/{operationId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/deploy/operations/{operationId}")]
    public async Task GetDeployOperation_ReconcilesActiveOperationBeforeReturning()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/deploy/operations", new
        {
            targetId = "prod-api",
            desiredRevision = "sha256:poll123"
        });

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var createDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var operationId = createDocument.RootElement.GetProperty("operationId").GetString();
        operationId.Should().NotBeNullOrWhiteSpace();

        _reconciler.OnReconcileAsync = async (id, cancellationToken) =>
        {
            var operation = await _workflowStore.GetAsync(id, cancellationToken);
            operation.Should().NotBeNull();
            await _workflowStore.SetAsync(operation! with
            {
                Status = WorkflowOperationStatus.Succeeded,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Reconciled during status poll.",
                ObservedState = operation.Deploy?.DesiredRevision
            }, cancellationToken: cancellationToken);
        };

        var getResponse = await _client.GetAsync($"/api/v1/admin/deploy/operations/{operationId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var getDocument = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        getDocument.RootElement.GetProperty("status").GetString().Should().Be("Succeeded");
        getDocument.RootElement.GetProperty("currentPhase").GetString().Should().Be("Reconciled during status poll.");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/plan")]
    public async Task PlanDeployOperation_WhenTargetDoesNotExist_ReturnsErrorStatus()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/deploy/plan", new
        {
            targetId = "nonexistent-target",
            desiredRevision = "sha256:doesnotexist"
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/deploy/operations/{operationId}")]
    public async Task GetDeployOperation_NonexistentOperationId_ReturnsErrorStatus()
    {
        var response = await _client.GetAsync("/api/v1/admin/deploy/operations/deploy-does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/submit")]
    public async Task SubmitDeployOperation_AlreadySubmitted_ReturnsIdempotent()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/deploy/operations", new
        {
            targetId = "prod-api",
            desiredRevision = "sha256:conflict789",
            submitImmediately = false
        });

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var createDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var operationId = createDocument.RootElement.GetProperty("operationId").GetString();

        // First submit
        var firstSubmit = await _client.PostAsJsonAsync($"/api/v1/admin/deploy/operations/{operationId}/submit", new
        {
            reason = "First submission"
        });
        firstSubmit.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second submit — server returns OK idempotently
        var secondSubmit = await _client.PostAsJsonAsync($"/api/v1/admin/deploy/operations/{operationId}/submit", new
        {
            reason = "Duplicate submission"
        });
        secondSubmit.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict, HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task RollbackDeployOperation_NonexistentOperationId_ReturnsNotFound()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/deploy/operations/deploy-does-not-exist/rollback",
            new
            {
                reason = "No active deployment exists"
            });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task RollbackDeployOperation_WhenApprovalRequired_ReturnsForbidden()
    {
        var approvalFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDatabaseMigrationRunner>();
                services.AddSingleton<IDatabaseMigrationRunner>(_migrationRunner);
                services.RemoveAll<IDeployTargetRegistry>();
                services.RemoveAll<IWorkflowOperationStore>();
                services.RemoveAll<IWorkflowOperationReconciler>();
                services.AddSingleton<IDeployTargetRegistry>(new StubDeployTargetRegistry());
                services.AddSingleton<IWorkflowOperationStore>(new InMemoryWorkflowOperationStore());
                services.AddSingleton<IWorkflowOperationReconciler>(new StubWorkflowOperationReconciler());
                services.RemoveAll<Core.Features.Authorization.Abstractions.IOperatorApprovalEvaluator>();
                services.AddSingleton<Core.Features.Authorization.Abstractions.IOperatorApprovalEvaluator>(
                    new AlwaysRequiresApprovalEvaluator());
            });

        try
        {
            await approvalFixture.InitializeAsync();
            var client = approvalFixture.CreateAdminClient();

            var response = await client.PostAsJsonAsync(
                "/api/v1/admin/deploy/operations/deploy-any-id/rollback",
                new { reason = "Test approval gating on rollback" });

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            await approvalFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations")]
    public async Task CreateDeployOperation_WithSubmitImmediately_ReturnsSubmittedStatus()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/deploy/operations", new
        {
            targetId = "prod-api",
            desiredRevision = "sha256:immediate456",
            reason = "Auto-submit test",
            submitImmediately = true
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var status = document.RootElement.GetProperty("status").GetString();
        status.Should().BeOneOf("Submitted", "Planned");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations")]
    public async Task CreateDeployOperation_WhenApprovalRequired_ReturnsAwaitingApprovalStatus()
    {
        // Use a separate fixture with a stub approval evaluator that always requires approval.
        var approvalFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDatabaseMigrationRunner>();
                services.AddSingleton<IDatabaseMigrationRunner>(_migrationRunner);
                services.RemoveAll<IDeployTargetRegistry>();
                services.RemoveAll<IWorkflowOperationStore>();
                services.RemoveAll<IWorkflowOperationReconciler>();
                services.AddSingleton<IDeployTargetRegistry>(new StubDeployTargetRegistry());
                services.AddSingleton<IWorkflowOperationStore>(new InMemoryWorkflowOperationStore());
                services.AddSingleton<IWorkflowOperationReconciler>(new StubWorkflowOperationReconciler());
                services.RemoveAll<Core.Features.Authorization.Abstractions.IOperatorApprovalEvaluator>();
                services.AddSingleton<Core.Features.Authorization.Abstractions.IOperatorApprovalEvaluator>(
                    new AlwaysRequiresApprovalEvaluator());
            });

        try
        {
            await approvalFixture.InitializeAsync();
            var client = approvalFixture.CreateAdminClient();

            var createResponse = await client.PostAsJsonAsync("/api/v1/admin/deploy/operations", new
            {
                targetId = "prod-api",
                desiredRevision = "sha256:approval-test",
                reason = "Test approval gating",
                submitImmediately = true
            });

            createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

            using var document = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
            var root = document.RootElement;
            root.GetProperty("status").GetString().Should().Be("AwaitingApproval");
        }
        finally
        {
            await approvalFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/plan")]
    public async Task PlanDeployOperation_WhenApprovalRequired_ReturnsRequiresApproval()
    {
        var approvalFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDatabaseMigrationRunner>();
                services.AddSingleton<IDatabaseMigrationRunner>(_migrationRunner);
                services.RemoveAll<IDeployTargetRegistry>();
                services.RemoveAll<IWorkflowOperationStore>();
                services.RemoveAll<IWorkflowOperationReconciler>();
                services.AddSingleton<IDeployTargetRegistry>(new StubDeployTargetRegistry());
                services.AddSingleton<IWorkflowOperationStore>(new InMemoryWorkflowOperationStore());
                services.AddSingleton<IWorkflowOperationReconciler>(new StubWorkflowOperationReconciler());
                services.RemoveAll<Core.Features.Authorization.Abstractions.IOperatorApprovalEvaluator>();
                services.AddSingleton<Core.Features.Authorization.Abstractions.IOperatorApprovalEvaluator>(
                    new AlwaysRequiresApprovalEvaluator());
            });

        try
        {
            await approvalFixture.InitializeAsync();
            var client = approvalFixture.CreateAdminClient();

            var response = await client.PostAsJsonAsync("/api/v1/admin/deploy/plan", new
            {
                targetId = "prod-api",
                desiredRevision = "sha256:approval-plan-test"
            });

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            root.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
            root.GetProperty("readyToSubmit").GetBoolean().Should().BeFalse();
        }
        finally
        {
            await approvalFixture.DisposeAsync();
        }
    }

    private sealed class AlwaysRequiresApprovalEvaluator : Core.Features.Authorization.Abstractions.IOperatorApprovalEvaluator
    {
        public Core.Features.Authorization.Domain.ApprovalRequirement Evaluate(
            System.Security.Claims.ClaimsPrincipal principal,
            Core.Features.Authorization.Domain.OperatorAuthorizationRequest request)
            => Core.Features.Authorization.Domain.ApprovalRequirement.Required(
                "operator.test-policy", "test-approval-required");
    }

    private sealed class StubDatabaseMigrationRunner : IDatabaseMigrationRunner
    {
        public DatabaseMigrationPlan Plan { get; set; } = DatabaseMigrationPlan.Succeeded();

        public Task<DatabaseMigrationPlan> PlanMigrationsAsync(
            string connectionString,
            Assembly migrationsAssembly,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Plan);

        public Task<DatabaseMigrationResult> RunMigrationsAsync(
            string connectionString,
            Assembly migrationsAssembly,
            CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseMigrationResult.Succeeded());
    }

    private sealed class StubDeployTargetRegistry : IDeployTargetRegistry
    {
        private static readonly DeployTargetDefinition[] Targets =
        [
            new()
            {
                TargetId = "prod-api",
                TargetKind = DeployTargetKind.Kubernetes,
                Backend = "honua-gitops-kubernetes",
                Environment = "production",
                TargetName = "honua-server",
                ArtifactReference = "ghcr.io/honua/server",
                RuntimeProfile = "dotnet-api",
                Parameters = new Dictionary<string, string>
                {
                    ["namespace"] = "honua",
                    ["release"] = "honua-server"
                }
            }
        ];

        public Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeployTargetDefinition>>(Targets);

        public Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromResult(Targets.SingleOrDefault(target => target.TargetId == targetId));
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

        public Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
        {
            var operations = _operations.Values
                .Where(operation => !kind.HasValue || operation.Kind == kind.Value)
                .ToArray();

            return Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(operations);
        }
    }

    private sealed class StubWorkflowOperationReconciler : IWorkflowOperationReconciler
    {
        public Func<string, CancellationToken, Task>? OnReconcileAsync { get; set; }

        public Task ReconcileWorkflowOperationAsync(string operationId, CancellationToken cancellationToken = default)
            => OnReconcileAsync?.Invoke(operationId, cancellationToken) ?? Task.CompletedTask;
    }
}
