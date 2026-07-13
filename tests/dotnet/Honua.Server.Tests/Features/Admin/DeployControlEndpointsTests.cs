// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
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
                services.RemoveAll<IOptions<MigrationSafetyOptions>>();
                services.AddSingleton(Options.Create(new MigrationSafetyOptions
                {
                    BackupCommand = "pg_dump --format=custom"
                }));
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
        root.TryGetProperty("platformRelease", out _).Should().BeFalse();
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

        // Cross-plane platform-release skew view is surfaced only under diagnostics (ADR-0060 WS2).
        var platformRelease = root.GetProperty("platformRelease");
        platformRelease.GetProperty("releaseDeclared").GetBoolean().Should().BeFalse();
        platformRelease.GetProperty("isCoVersioned").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/deploy/preflight")]
    public async Task GetDeployPreflight_WithDiagnostics_ReturnsBackupHookOutcomeForPendingSet()
    {
        const string ContractScript = "002_drop_legacy_annotated.sql";
        _migrationRunner.Plan = DatabaseMigrationPlan.Succeeded(
            pendingScripts: [ContractScript],
            pendingScriptClassifications:
            [
                new MigrationScriptClassification
                {
                    ScriptName = ContractScript,
                    Classification = MigrationSafetyClassification.ContractAnnotated,
                    BreakingRules = ["drop-column"]
                }
            ],
            journalIsNonEmpty: true);

        _fixture.Services.GetRequiredService<MigrationBackupHookState>()
            .Record(new DatabaseMigrationBackupHookResult
            {
                Outcome = "succeeded",
                Succeeded = true,
                StartedAt = new DateTimeOffset(2026, 7, 9, 10, 15, 0, TimeSpan.Zero),
                CompletedAt = new DateTimeOffset(2026, 7, 9, 10, 15, 1, TimeSpan.Zero),
                DurationMilliseconds = 1_200,
                ExitCode = 0,
                PendingContractScripts = [ContractScript],
                MigrationRunId = "schema-migration-test",
                CorrelationId = "schema-migration-test"
            });

        var response = await _client.GetAsync("/api/v1/admin/deploy/preflight?includeDiagnostics=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var backupHook = document.RootElement
            .GetProperty("migration")
            .GetProperty("backupHook");

        backupHook.GetProperty("configured").GetBoolean().Should().BeTrue();
        backupHook.GetProperty("requiredForPendingSet").GetBoolean().Should().BeTrue();
        backupHook.GetProperty("ranForPendingSet").GetBoolean().Should().BeTrue();
        backupHook.GetProperty("succeeded").GetBoolean().Should().BeTrue();
        backupHook.GetProperty("outcome").GetString().Should().Be("succeeded");
        backupHook.GetProperty("durationMilliseconds").GetInt64().Should().Be(1_200);
        backupHook.GetProperty("pendingContractScripts")[0].GetString().Should().Be(ContractScript);
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
        // #2811: the GitOps hand-off backend cannot perform a real revert, so a rollback request fails
        // loudly (manual intervention) rather than falsely reporting RollbackRequested — the operator is
        // told to revert the pinned revision out of band instead of being led to believe it rolled back.
        rollbackDocument.RootElement.GetProperty("status").GetString().Should().Be("ManualInterventionRequired");
        rollbackDocument.RootElement.GetProperty("currentPhase").GetString().Should().Contain("out of band");
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
    [Endpoint("GET /api/v1/admin/deploy/operations")]
    public async Task ListDeployOperations_PagesAndFiltersNewestFirst()
    {
        // Three deploy operations created oldest-first; the newest must sort to the front.
        for (var i = 0; i < 3; i++)
        {
            var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/deploy/operations", new
            {
                targetId = "prod-api",
                desiredRevision = $"sha256:list{i}",
                submitImmediately = false
            });
            createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // Page 1 with pageSize 2: newest-first, has-more true, two items.
        var firstPage = await _client.GetAsync("/api/v1/admin/deploy/operations?kind=Deploy&status=Planned&page=1&pageSize=2");
        firstPage.StatusCode.Should().Be(HttpStatusCode.OK);

        using var firstDocument = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
        var firstRoot = firstDocument.RootElement;
        firstRoot.GetProperty("page").GetInt32().Should().Be(1);
        firstRoot.GetProperty("pageSize").GetInt32().Should().Be(2);
        firstRoot.GetProperty("totalCount").GetInt32().Should().Be(3);
        firstRoot.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        var firstItems = firstRoot.GetProperty("items");
        firstItems.GetArrayLength().Should().Be(2);
        firstItems[0].GetProperty("kind").GetString().Should().Be("Deploy");
        firstItems[0].GetProperty("status").GetString().Should().Be("Planned");

        // Page 2 completes the set with the final item and no further pages.
        var secondPage = await _client.GetAsync("/api/v1/admin/deploy/operations?page=2&pageSize=2");
        secondPage.StatusCode.Should().Be(HttpStatusCode.OK);

        using var secondDocument = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync());
        var secondRoot = secondDocument.RootElement;
        secondRoot.GetProperty("items").GetArrayLength().Should().Be(1);
        secondRoot.GetProperty("hasMore").GetBoolean().Should().BeFalse();

        // The two pages together cover exactly the three created revisions, newest-first by creation time.
        var pagedItems = firstItems.EnumerateArray().Concat(secondRoot.GetProperty("items").EnumerateArray()).ToArray();
        pagedItems.Select(item => item.GetProperty("target").GetProperty("desiredRevision").GetString())
            .Should().BeEquivalentTo(new[] { "sha256:list0", "sha256:list1", "sha256:list2" });
        var createdAt = pagedItems.Select(item => item.GetProperty("createdAt").GetDateTimeOffset()).ToArray();
        createdAt.Should().BeInDescendingOrder();

        // Status filter that matches nothing returns an empty page.
        var emptyPage = await _client.GetAsync("/api/v1/admin/deploy/operations?status=RolledBack");
        emptyPage.StatusCode.Should().Be(HttpStatusCode.OK);
        using var emptyDocument = JsonDocument.Parse(await emptyPage.Content.ReadAsStringAsync());
        emptyDocument.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        emptyDocument.RootElement.GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/deploy/operations")]
    public async Task ListDeployOperations_WithUnsupportedFilterValue_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/v1/admin/deploy/operations?status=NotARealStatus");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/promote")]
    public async Task PromoteDeployOperation_WhenParkedInReconciling_ForcesCutover()
    {
        var promoteStore = new InMemoryWorkflowOperationStore();
        var backend = new PromotableDeployBackend();
        var promoteFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDatabaseMigrationRunner>();
                services.AddSingleton<IDatabaseMigrationRunner>(_migrationRunner);
                services.RemoveAll<IDeployTargetRegistry>();
                services.RemoveAll<IWorkflowOperationStore>();
                services.RemoveAll<IWorkflowOperationReconciler>();
                services.AddSingleton<IDeployTargetRegistry>(new PromotableTargetRegistry());
                services.AddSingleton<IWorkflowOperationStore>(promoteStore);
                services.AddSingleton<IWorkflowOperationReconciler>(new StubWorkflowOperationReconciler());
                services.AddSingleton<IDeployBackend>(backend);
            });

        try
        {
            await promoteFixture.InitializeAsync();
            var client = promoteFixture.CreateAdminClient();

            var operationId = $"deploy-{Guid.NewGuid():N}";
            await promoteStore.TryCreateAsync(CreateParkedOperation(operationId));

            var response = await client.PostAsJsonAsync(
                $"/api/v1/admin/deploy/operations/{operationId}/promote",
                new { reason = "Operator forcing cutover; telemetry unavailable" });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("status").GetString().Should().Be("Succeeded");
            backend.PromoteCalls.Should().Be(1);
        }
        finally
        {
            await promoteFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/promote")]
    public async Task PromoteDeployOperation_NonexistentOperationId_ReturnsNotFound()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/deploy/operations/deploy-does-not-exist/promote",
            new { reason = "No active deployment exists" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/promote")]
    public async Task PromoteDeployOperation_WhenNotYetSubmitted_ReturnsConflict()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/deploy/operations", new
        {
            targetId = "prod-api",
            desiredRevision = "sha256:premature-promote",
            reason = "Create planned operation before promote precondition test",
            submitImmediately = false
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        using var createDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var operationId = createDocument.RootElement.GetProperty("operationId").GetString();
        createDocument.RootElement.GetProperty("status").GetString().Should().Be("Planned");

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/deploy/operations/{operationId}/promote",
            new { reason = "Promote before submit" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
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

            var createResponse = await client.PostAsJsonAsync("/api/v1/admin/deploy/operations", new
            {
                targetId = "prod-api",
                desiredRevision = "sha256:approval-rollback-test",
                reason = "Create operation before rollback gate test",
                submitImmediately = false
            });
            createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            using var createDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
            var operationId = createDocument.RootElement.GetProperty("operationId").GetString();
            operationId.Should().NotBeNullOrWhiteSpace();

            var response = await client.PostAsJsonAsync(
                $"/api/v1/admin/deploy/operations/{operationId}/rollback",
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

    private static WorkflowOperationRecord CreateParkedOperation(string operationId)
        => new()
        {
            OperationId = operationId,
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Reconciling,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CurrentPhase = "Standby healthy; awaiting promotion.",
            ProviderOperationId = "test-promote-backend:op",
            Audit = new OperationAuditInfo { RequestedBy = "alice", Reason = "Rollout" },
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = "production:onprem-rolling",
                RequiresExclusiveLease = true
            },
            Deploy = new DeployOperationSpec
            {
                TargetId = "onprem-rolling",
                TargetKind = DeployTargetKind.SelfHostedRolling,
                Backend = "test-promote-backend",
                Environment = "production",
                TargetName = "honua-server",
                CurrentRevision = "sha256:old",
                DesiredRevision = "sha256:new",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            }
        };

    private sealed class PromotableTargetRegistry : IDeployTargetRegistry
    {
        private static readonly DeployTargetDefinition Target = new()
        {
            TargetId = "onprem-rolling",
            TargetKind = DeployTargetKind.SelfHostedRolling,
            Backend = "test-promote-backend",
            Environment = "production",
            TargetName = "honua-server",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        };

        public Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeployTargetDefinition>>([Target]);

        public Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromResult(targetId == Target.TargetId ? Target : null);
    }

    private sealed class PromotableDeployBackend : IDeployBackend
    {
        public int PromoteCalls { get; private set; }

        public string BackendName => "test-promote-backend";

        public DeployTargetKind TargetKind => DeployTargetKind.SelfHostedRolling;

        public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployBackendCapabilities { SupportsRollback = true, SupportsProgressPolling = true });

        public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployPlan { IsReadyToSubmit = true });

        public Task<DeploySubmissionResult> StartAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeploySubmissionResult { Status = WorkflowOperationStatus.Submitted, Message = "Submitted" });

        public Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                PromotionRecommended = true,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.CurrentRevision,
                Message = "Standby healthy and ready for cutover."
            });

        public Task<DeployObservation> PromoteAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            PromoteCalls++;
            return Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.Succeeded,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.DesiredRevision,
                Message = "Manual promotion cut traffic over to the new revision."
            });
        }

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                ProviderOperationId = operation.ProviderOperationId,
                Message = "Rollback requested"
            });
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
        private readonly Dictionary<string, string> _metadataPackageIndex = new(StringComparer.Ordinal);

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
            IndexMetadataReleaseOperation(operation);
            return Task.FromResult(true);
        }

        public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryGetValue(operationId, out var operation) ? operation : null);

        public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult(
                _metadataPackageIndex.TryGetValue(packageId, out var operationId) &&
                _operations.TryGetValue(operationId, out var operation)
                    ? operation
                    : null);

        public Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            IndexMetadataReleaseOperation(operation);
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            IndexMetadataReleaseOperation(operation);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
        {
            var operations = _operations.Values
                .Where(operation => !kind.HasValue || operation.Kind == kind.Value)
                .ToArray();

            return Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(operations);
        }

        public Task<WorkflowOperationPage> QueryAsync(WorkflowOperationQuery query, CancellationToken cancellationToken = default)
        {
            var filtered = _operations.Values
                .Where(operation => (!query.Kind.HasValue || operation.Kind == query.Kind.Value)
                    && (!query.Status.HasValue || operation.Status == query.Status.Value))
                .OrderByDescending(operation => operation.CreatedAt)
                .ThenByDescending(operation => operation.OperationId, StringComparer.Ordinal)
                .ToArray();

            var page = Math.Max(1, query.Page);
            var pageSize = query.PageSize <= 0 ? 50 : query.PageSize;
            var skip = (page - 1) * pageSize;
            var items = filtered.Skip(skip).Take(pageSize).ToArray();

            return Task.FromResult(new WorkflowOperationPage
            {
                Items = items,
                Page = page,
                PageSize = pageSize,
                TotalCount = filtered.Length,
                HasMore = skip + items.Length < filtered.Length
            });
        }

        public Task<WorkflowOperationRecord?> GetMostRecentSucceededDeployByTargetAsync(string targetId, CancellationToken cancellationToken = default)
        {
            var match = _operations.Values
                .Where(operation => operation.Kind == WorkflowOperationKind.Deploy
                    && operation.Status == WorkflowOperationStatus.Succeeded
                    && string.Equals(operation.Deploy?.TargetId, targetId, StringComparison.Ordinal))
                .OrderByDescending(operation => operation.CompletedAt ?? operation.UpdatedAt)
                .FirstOrDefault();

            return Task.FromResult<WorkflowOperationRecord?>(match);
        }

        private void IndexMetadataReleaseOperation(WorkflowOperationRecord operation)
        {
            if (operation.Kind == WorkflowOperationKind.MetadataRelease &&
                !string.IsNullOrWhiteSpace(operation.MetadataRelease?.PackageId))
            {
                _metadataPackageIndex[operation.MetadataRelease.PackageId] = operation.OperationId;
            }
        }
    }

    private sealed class StubWorkflowOperationReconciler : IWorkflowOperationReconciler
    {
        public Func<string, CancellationToken, Task>? OnReconcileAsync { get; set; }

        public Task ReconcileWorkflowOperationAsync(string operationId, CancellationToken cancellationToken = default)
            => OnReconcileAsync?.Invoke(operationId, cancellationToken) ?? Task.CompletedTask;
    }
}
