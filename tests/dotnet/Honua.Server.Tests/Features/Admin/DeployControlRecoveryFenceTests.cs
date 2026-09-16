// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Admin.Deploy;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// honua-server#4958: the rollback surface is fenced to a declared, scoped, non-expired recovery grant.
/// Every case here drives the real <c>POST /api/v1/admin/deploy/operations/{operationId}/rollback</c>
/// endpoint against a real protected activation — no injected authorization result, no seam standing in
/// for the fence — and every refusal asserts both the stable machine-readable code and that the
/// operation did <b>not</b> transition. On the pinned candidate (886206527cc97bad1bbaa5fa6358910ebc45e9c0)
/// each of these bodies returned HTTP 200 and settled the operation with every fence term dropped.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class DeployControlRecoveryFenceTests : IAsyncLifetime
{
    private const string TargetId = "fence-target";

    private readonly FenceWorkflowOperationStore _store = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public DeployControlRecoveryFenceTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDatabaseMigrationRunner>();
                services.AddSingleton<IDatabaseMigrationRunner>(new FenceMigrationRunner());
                services.RemoveAll<IDeployTargetRegistry>();
                services.AddSingleton<IDeployTargetRegistry>(new FenceTargetRegistry());
                services.RemoveAll<IWorkflowOperationStore>();
                services.AddSingleton<IWorkflowOperationStore>(_store);
                services.RemoveAll<IWorkflowOperationReconciler>();
                services.AddSingleton<IWorkflowOperationReconciler>(new FenceReconciler());
                services.AddSingleton<IDeployBackend>(new FenceDeployBackend());
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/deploy/operations/{operationId}")]
    public async Task ProtectedActivation_SealsTheRecoveryGrantOnTheProtectionRecord()
    {
        var activation = await CreateProtectedActivationAsync();

        // The terms a caller has to quote are published, not guessed: honua-devops#191 reads them from
        // this record. The pinned candidate published none of grantId/actor/permittedCompensation.
        activation.Protection.GetProperty("grantId").GetString().Should().StartWith("grant-");
        activation.Protection.GetProperty("actor").GetString().Should().NotBeNullOrWhiteSpace();
        activation.Protection.GetProperty("permittedCompensation").GetString()
            .Should().Be(DeployRecoveryCompensations.RestorePreviousRevision);
        activation.Protection.GetProperty("policyDigest").GetString().Should().NotBeNullOrWhiteSpace();
        activation.Protection.GetProperty("candidateRevision").GetString().Should().Be(activation.CandidateRevision);
        activation.Protection.GetProperty("phase").GetString().Should().Be("observing");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task Rollback_WithSatisfiedFence_IsAdmitted()
    {
        var activation = await CreateProtectedActivationAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/deploy/operations/{activation.OperationId}/rollback",
            activation.SatisfiedFence());

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetString().Should().Be("RolledBack");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task Rollback_WithNoFence_KeepsPreFenceBehaviourForExistingCallers()
    {
        var activation = await CreateProtectedActivationAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/deploy/operations/{activation.OperationId}/rollback",
            new { reason = "unfenced legacy caller" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task Rollback_WithForeignTarget_IsRefusedWithoutTransition()
        => await AssertRefusedAsync(
            fence => fence with { TargetId = "some-other-target" },
            HttpStatusCode.Conflict,
            RecoveryGrantFence.TargetMismatchCode);

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task Rollback_WithWrongExpectedCandidateRevision_IsRefusedWithoutTransition()
        => await AssertRefusedAsync(
            fence => fence with { ExpectedCandidateRevision = "rev-that-is-not-serving" },
            HttpStatusCode.Conflict,
            RecoveryGrantFence.CandidateRevisionMismatchCode);

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task Rollback_WithWrongExpectedPreviousRevision_IsRefusedWithoutTransition()
        => await AssertRefusedAsync(
            fence => fence with { ExpectedPreviousRevision = "nonsense" },
            HttpStatusCode.Conflict,
            RecoveryGrantFence.PreviousRevisionMismatchCode);

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task Rollback_WithForeignActor_IsRefusedWithoutTransition()
        => await AssertRefusedAsync(
            fence => fence with { Actor = "nobody@example.invalid" },
            HttpStatusCode.Forbidden,
            RecoveryGrantFence.ActorMismatchCode);

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task Rollback_WithForeignTenant_IsRefusedWithoutTransition()
        => await AssertRefusedAsync(
            fence => fence with { TenantId = "tenant-not-mine" },
            HttpStatusCode.Forbidden,
            RecoveryGrantFence.TenantMismatchCode);

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task Rollback_WithUnknownGrantId_IsRefusedWithoutTransition()
        => await AssertRefusedAsync(
            fence => fence with { GrantId = "grant-00000000" },
            HttpStatusCode.Conflict,
            RecoveryGrantFence.GrantMismatchCode);

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task Rollback_WithMismatchedPolicyDigest_IsRefusedWithoutTransition()
        => await AssertRefusedAsync(
            fence => fence with { PolicyDigest = "0000" },
            HttpStatusCode.Conflict,
            RecoveryGrantFence.PolicyDigestMismatchCode);

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task Rollback_WithWrongProtectionPhase_IsRefusedWithoutTransition()
        => await AssertRefusedAsync(
            fence => fence with { ExpectedProtectionPhase = "recovering" },
            HttpStatusCode.Conflict,
            RecoveryGrantFence.ProtectionPhaseMismatchCode);

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task Rollback_WithExpiredGrant_IsRefusedWithoutTransition()
        => await AssertRefusedAsync(
            fence => fence with { NotAfter = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero) },
            HttpStatusCode.PreconditionFailed,
            RecoveryGrantFence.ExpiredCode);

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task Rollback_WithBroadenedCompensation_IsRefusedWithoutTransition()
        => await AssertRefusedAsync(
            fence => fence with { Compensation = "delete-everything" },
            HttpStatusCode.Forbidden,
            RecoveryGrantFence.CompensationNotPermittedCode);

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task Rollback_WithUnrecognizedProtectionPhase_IsRefusedWithoutTransition()
        => await AssertRefusedAsync(
            fence => fence with { ExpectedProtectionPhase = "totally-made-up" },
            HttpStatusCode.BadRequest,
            RecoveryGrantFence.ProtectionPhaseUnrecognizedCode);

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task Rollback_WithGrantTermsButNoProtectionWindow_IsRefusedWithoutTransition()
    {
        // The pinned candidate's step 1/2: an operation that was never promoted has no window at all,
        // yet a rollback quoting a full grant was admitted and settled it.
        var operationId = await CreateSubmittedOperationAsync("rev-never-promoted");
        var before = await GetOperationAsync(operationId);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/deploy/operations/{operationId}/rollback",
            new
            {
                reason = "case-a rollback",
                grantId = "grant-00000000",
                policyDigest = "0000",
                expectedCandidateRevision = "rev-that-is-not-serving",
            });

        await AssertRefusalAsync(
            response,
            operationId,
            before,
            HttpStatusCode.Conflict,
            RecoveryGrantFence.ProtectionWindowAbsentCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task Rollback_WithUnknownProperty_IsRefusedWithoutTransition()
    {
        // Ask 4: the pinned candidate's binder silently dropped every one of these. `expectedCurrentRevision`
        // is exactly the name the honua-devops#191 probe sent believing it was fencing the rollback.
        var activation = await CreateProtectedActivationAsync();
        var before = await GetOperationAsync(activation.OperationId);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/deploy/operations/{activation.OperationId}/rollback",
            new { reason = "typo'd fence", expectedCurrentRevision = "rev-prior-1" });

        await AssertRefusalAsync(
            response,
            activation.OperationId,
            before,
            HttpStatusCode.BadRequest,
            RecoveryGrantFence.UnknownPropertyCode);
        (await response.Content.ReadAsStringAsync()).Should().Contain("expectedCurrentRevision");
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void Fence_BindsARecordedActorEvenWhenTheCallerDeclaresNoFence()
    {
        // Asks 2: identity binding is not opt-in. A foreign principal that simply stays silent must not
        // be able to actuate a grant sealed for somebody else.
        var operation = ProtectedRecord(actor: "ops-agent", tenantId: "tenant-a");

        RecoveryGrantFence.Evaluate(operation, new RollbackDeployOperationRequest { Reason = "silent" },
            authenticatedActor: "intruder", callerTenantId: "tenant-a",
            isPlatformAdministrator: false, DateTimeOffset.UtcNow)
            !.Code.Should().Be(RecoveryGrantFence.ActorMismatchCode);

        RecoveryGrantFence.Evaluate(operation, new RollbackDeployOperationRequest { Reason = "silent" },
            authenticatedActor: "ops-agent", callerTenantId: "tenant-b",
            isPlatformAdministrator: false, DateTimeOffset.UtcNow)
            !.Code.Should().Be(RecoveryGrantFence.TenantMismatchCode);

        RecoveryGrantFence.Evaluate(operation, new RollbackDeployOperationRequest { Reason = "silent" },
            authenticatedActor: "ops-agent", callerTenantId: "tenant-a",
            isPlatformAdministrator: false, DateTimeOffset.UtcNow)
            .Should().BeNull("the recorded actor and tenant may actuate their own grant");

        RecoveryGrantFence.Evaluate(operation, new RollbackDeployOperationRequest { Reason = "break glass" },
            authenticatedActor: "intruder", callerTenantId: "tenant-b",
            isPlatformAdministrator: true, DateTimeOffset.UtcNow)
            .Should().BeNull("an explicitly broader platform role may act outside the grant's binding");
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void Fence_OnASingleTenantInstallation_IsPurelyActorBound()
    {
        // "Keep single-tenant behaviour unchanged": tenant resolution off means no tenant is ever
        // recorded, so no rollback is ever refused for a tenant reason on such an installation.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "admin"), new Claim("tenant_id", "tenant-a")], "Test"));

        Honua.Server.Features.Admin.PlatformDeployAuthority
            .ResolveTenantId(principal, new TenantContextOptions { Enabled = false })
            .Should().BeNull();

        var operation = ProtectedRecord(actor: "admin", tenantId: null);
        RecoveryGrantFence.Evaluate(operation, new RollbackDeployOperationRequest { Reason = "single tenant" },
            authenticatedActor: "admin", callerTenantId: null,
            isPlatformAdministrator: false, DateTimeOffset.UtcNow)
            .Should().BeNull();
    }

    private static WorkflowOperationRecord ProtectedRecord(string? actor, string? tenantId)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowOperationRecord
        {
            OperationId = "deploy-fence-unit",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Reconciling,
            CreatedAt = now,
            UpdatedAt = now,
            Deploy = new DeployOperationSpec
            {
                TargetId = TargetId,
                TargetKind = DeployTargetKind.Kubernetes,
                Backend = "fence-backend",
                Environment = "probe",
                TargetName = "honua-server",
                DesiredRevision = "rev-candidate-3",
                Protection = new DeployProtectionState
                {
                    PreviousRevision = "rev-prior-1",
                    CandidateRevision = "rev-candidate-3",
                    FirstExposureAt = now,
                    ObservationDeadline = now.AddMinutes(10),
                    PolicyDigest = "DIGEST",
                    GrantId = "grant-abc",
                    Actor = actor,
                    TenantId = tenantId,
                    PermittedCompensation = DeployRecoveryCompensations.RestorePreviousRevision,
                }
            }
        };
    }

    private async Task AssertRefusedAsync(
        Func<RecoveryFenceBody, RecoveryFenceBody> corrupt,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        var activation = await CreateProtectedActivationAsync();
        var before = await GetOperationAsync(activation.OperationId);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/deploy/operations/{activation.OperationId}/rollback",
            corrupt(activation.SatisfiedFence()));

        await AssertRefusalAsync(response, activation.OperationId, before, expectedStatus, expectedCode);
    }

    private async Task AssertRefusalAsync(
        HttpResponseMessage response,
        string operationId,
        JsonElement before,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expectedStatus, payload);

        using var problem = JsonDocument.Parse(payload);
        problem.RootElement.GetProperty("code").GetString().Should().Be(expectedCode);

        // A refusal at admission leaves nothing behind: no status change, no attacker-supplied reason
        // durably recorded, no later transition for an operator to unwind.
        var after = await GetOperationAsync(operationId);
        after.GetProperty("status").GetString().Should().Be(before.GetProperty("status").GetString());
        after.GetProperty("updatedAt").GetDateTimeOffset().Should().Be(before.GetProperty("updatedAt").GetDateTimeOffset());
        after.TryGetProperty("completedAt", out _).Should().Be(before.TryGetProperty("completedAt", out _));
    }

    private async Task<JsonElement> GetOperationAsync(string operationId)
    {
        var response = await _client.GetAsync($"/api/v1/admin/deploy/operations/{operationId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private async Task<string> CreateSubmittedOperationAsync(string desiredRevision)
    {
        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/deploy/operations", new
        {
            targetId = TargetId,
            desiredRevision,
            currentRevision = "rev-prior-1",
            reason = "recovery fence coverage",
            submitImmediately = false,
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, await createResponse.Content.ReadAsStringAsync());
        using var createDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var operationId = createDocument.RootElement.GetProperty("operationId").GetString()!;

        var submitResponse = await _client.PostAsJsonAsync(
            $"/api/v1/admin/deploy/operations/{operationId}/submit", new { reason = "approved" });
        submitResponse.StatusCode.Should().Be(HttpStatusCode.OK, await submitResponse.Content.ReadAsStringAsync());
        return operationId;
    }

    private async Task<ProtectedActivation> CreateProtectedActivationAsync()
    {
        var candidateRevision = $"rev-candidate-{Guid.NewGuid():N}";
        var operationId = await CreateSubmittedOperationAsync(candidateRevision);

        var promoteResponse = await _client.PostAsJsonAsync(
            $"/api/v1/admin/deploy/operations/{operationId}/promote", new { reason = "cutover" });
        promoteResponse.StatusCode.Should().Be(HttpStatusCode.OK, await promoteResponse.Content.ReadAsStringAsync());

        var operation = await GetOperationAsync(operationId);
        var protection = operation.GetProperty("protection");
        protection.ValueKind.Should().Be(JsonValueKind.Object, "promotion opens a protection window");
        return new ProtectedActivation(operationId, candidateRevision, protection);
    }

    private sealed record ProtectedActivation(string OperationId, string CandidateRevision, JsonElement Protection)
    {
        /// <summary>Every fence term quoted from what the activation actually sealed — the positive control.</summary>
        public RecoveryFenceBody SatisfiedFence() => new(
            Reason: "telemetry breach",
            TargetId: TargetId,
            ExpectedCandidateRevision: Protection.GetProperty("candidateRevision").GetString(),
            ExpectedPreviousRevision: Protection.GetProperty("previousRevision").GetString(),
            ExpectedProtectionPhase: Protection.GetProperty("phase").GetString(),
            GrantId: Protection.GetProperty("grantId").GetString(),
            PolicyDigest: Protection.GetProperty("policyDigest").GetString(),
            Actor: Protection.GetProperty("actor").GetString(),
            TenantId: null,
            NotAfter: DateTimeOffset.UtcNow.AddMinutes(30),
            Compensation: Protection.GetProperty("permittedCompensation").GetString());
    }

    /// <summary>Wire shape of the recovery fence, so each case corrupts exactly one term.</summary>
    public sealed record RecoveryFenceBody(
        string Reason,
        string? TargetId,
        string? ExpectedCandidateRevision,
        string? ExpectedPreviousRevision,
        string? ExpectedProtectionPhase,
        string? GrantId,
        string? PolicyDigest,
        string? Actor,
        string? TenantId,
        DateTimeOffset? NotAfter,
        string? Compensation);

    private sealed class FenceDeployBackend : IDeployBackend
    {
        public string BackendName => "fence-backend";

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
            => Task.FromResult(new DeployObservation { Status = WorkflowOperationStatus.RolledBack, Message = "Rolled back" });
    }

    private sealed class FenceTargetRegistry : IDeployTargetRegistry
    {
        private static readonly DeployTargetDefinition Target = new()
        {
            TargetId = TargetId,
            TargetKind = DeployTargetKind.Kubernetes,
            Backend = "fence-backend",
            Environment = "probe",
            TargetName = "honua-server",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        };

        public Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeployTargetDefinition>>([Target]);

        public Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromResult(targetId == Target.TargetId ? Target : null);
    }

    private sealed class FenceReconciler : IWorkflowOperationReconciler
    {
        public Task ReconcileWorkflowOperationAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FenceMigrationRunner : IDatabaseMigrationRunner
    {
        public Task<DatabaseMigrationPlan> PlanMigrationsAsync(string connectionString, Assembly migrationsAssembly, CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseMigrationPlan.Succeeded());

        public Task<DatabaseMigrationResult> RunMigrationsAsync(string connectionString, Assembly migrationsAssembly, CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseMigrationResult.Succeeded());
    }

    private sealed class FenceWorkflowOperationStore : IWorkflowOperationStore
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
            lock (_operations)
            {
                if (_operations.ContainsKey(operation.OperationId))
                {
                    return Task.FromResult(false);
                }

                _operations[operation.OperationId] = operation;
                return Task.FromResult(true);
            }
        }

        public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
        {
            lock (_operations)
            {
                return Task.FromResult(_operations.TryGetValue(operationId, out var operation) ? operation : null);
            }
        }

        public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkflowOperationRecord?>(null);

        public Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            lock (_operations)
            {
                _operations[operation.OperationId] = operation;
            }

            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            lock (_operations)
            {
                _operations[operation.OperationId] = operation;
            }

            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
        {
            lock (_operations)
            {
                return Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(
                    _operations.Values.Where(op => !kind.HasValue || op.Kind == kind.Value).ToArray());
            }
        }
    }
}
