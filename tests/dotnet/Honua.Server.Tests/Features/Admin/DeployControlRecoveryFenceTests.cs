// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
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
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

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
    private const string Issuer = "https://recovery-fence.test";
    private const string Audience = "recovery-fence-client";
    private const string SigningKey = "recovery-fence-cross-tenant-signing-key-32!";

    // honua-server#4987: two tenant-bound platform administrators, exactly as the live reproduction
    // minted them, plus a tenant-bound admin that holds no platform role.
    private static readonly FencePrincipal TenantAPlatformAdmin = new("ops-a", "tenant-a", ["admin", "platform_admin"]);
    private static readonly FencePrincipal TenantBPlatformAdmin = new("ops-b", "tenant-b", ["admin", "platform_admin"]);
    private static readonly FencePrincipal TenantCAdmin = new("ops-c", "tenant-c", ["admin"]);

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
            })
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("Authentication:ClientCertificates:Mode", "Optional");
                builder.UseSetting("Oidc:Enabled", "true");
                builder.UseSetting("Oidc:RequireHttps", "true");
                builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", SigningKey);
                builder.UseSetting("Oidc:TokenValidation:EnableTokenReplayProtection", "false");
                builder.UseSetting("Oidc:Generic:Enabled", "true");
                builder.UseSetting("Oidc:Generic:Authority", Issuer);
                builder.UseSetting("Oidc:Generic:ClientId", Audience);
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

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task Rollback_ByAnotherTenantsPlatformAdministratorDeclaringItsOwnIdentity_IsRefusedWithoutTransition()
    {
        // honua-server#4987 request 2: a complete fence quoting tenant-a's sealed grant while declaring
        // tenant-b's own actor and tenant. nightly-2cc2213 returned 200 and rolled tenant-a back.
        using var tenantA = CreateBearerClient(TenantAPlatformAdmin);
        using var tenantB = CreateBearerClient(TenantBPlatformAdmin);
        var activation = await CreateProtectedActivationAsync(tenantA);
        activation.Protection.GetProperty("actor").GetString().Should().Be(TenantAPlatformAdmin.Subject);
        activation.Protection.GetProperty("tenantId").GetString().Should().Be(TenantAPlatformAdmin.TenantId);
        var before = await GetOperationAsync(activation.OperationId);

        var response = await tenantB.PostAsJsonAsync(
            $"/api/v1/admin/deploy/operations/{activation.OperationId}/rollback",
            activation.SatisfiedFence() with
            {
                Actor = TenantBPlatformAdmin.Subject,
                TenantId = TenantBPlatformAdmin.TenantId,
            });

        await AssertRefusalAsync(
            response,
            activation.OperationId,
            before,
            HttpStatusCode.Forbidden,
            RecoveryGrantFence.ActorMismatchCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task Rollback_ByAnotherTenantsPlatformAdministratorWithNoFence_IsRefusedWithoutTransition()
    {
        // honua-server#4987 request 3: no fence at all. A platform role is no exemption from the sealed
        // binding, and silence is not a way around it.
        using var tenantA = CreateBearerClient(TenantAPlatformAdmin);
        using var tenantB = CreateBearerClient(TenantBPlatformAdmin);
        var activation = await CreateProtectedActivationAsync(tenantA);
        var before = await GetOperationAsync(activation.OperationId);

        var response = await tenantB.PostAsJsonAsync(
            $"/api/v1/admin/deploy/operations/{activation.OperationId}/rollback",
            new { reason = "unfenced cross-tenant compensation" });

        await AssertRefusalAsync(
            response,
            activation.OperationId,
            before,
            HttpStatusCode.Forbidden,
            RecoveryGrantFence.ActorMismatchCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task Rollback_BySealedTenantBoundPrincipalWithSatisfiedFence_IsAdmitted()
    {
        // The legitimate same-tenant path: the principal the grant was sealed for, quoting its own grant
        // including its tenant, is admitted.
        using var tenantA = CreateBearerClient(TenantAPlatformAdmin);
        var activation = await CreateProtectedActivationAsync(tenantA);

        var response = await tenantA.PostAsJsonAsync(
            $"/api/v1/admin/deploy/operations/{activation.OperationId}/rollback",
            activation.SatisfiedFence() with { TenantId = activation.Protection.GetProperty("tenantId").GetString() });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetString().Should().Be("RolledBack");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/deploy/operations/{operationId}")]
    [Endpoint("GET /api/v1/admin/deploy/operations")]
    public async Task Reads_ByTenantBoundAdminWithoutPlatformRole_DoNotPublishTheSealedGrantIdentity()
    {
        // honua-server#4987 secondary: a tenant-bound admin without a platform role read tenant-a's grantId,
        // actor and tenant. It can actuate no compensation, so it is shown none of the grant's identity.
        using var tenantA = CreateBearerClient(TenantAPlatformAdmin);
        using var tenantC = CreateBearerClient(TenantCAdmin);
        var activation = await CreateProtectedActivationAsync(tenantA);

        var single = await tenantC.GetAsync($"/api/v1/admin/deploy/operations/{activation.OperationId}");
        single.StatusCode.Should().Be(HttpStatusCode.OK, await single.Content.ReadAsStringAsync());
        using (var document = JsonDocument.Parse(await single.Content.ReadAsStringAsync()))
        {
            AssertGrantIdentityRedacted(document.RootElement.GetProperty("protection"));
        }

        var list = await tenantC.GetAsync("/api/v1/admin/deploy/operations?pageSize=200");
        list.StatusCode.Should().Be(HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        using (var document = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            var item = document.RootElement.GetProperty("items").EnumerateArray()
                .Single(candidate => candidate.GetProperty("operationId").GetString() == activation.OperationId);
            AssertGrantIdentityRedacted(item.GetProperty("protection"));
        }

        // The sealed principal still reads the terms it has to quote back.
        var own = await GetOperationAsync(activation.OperationId, tenantA);
        own.GetProperty("protection").GetProperty("grantId").GetString()
            .Should().Be(activation.Protection.GetProperty("grantId").GetString());
    }

    private static void AssertGrantIdentityRedacted(JsonElement protection)
    {
        foreach (var property in new[] { "grantId", "actor", "tenantId" })
        {
            (protection.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null)
                .Should().BeFalse($"'{property}' is part of the sealed grant identity");
        }

        protection.GetProperty("phase").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void Fence_BindsARecordedActorEvenWhenTheCallerDeclaresNoFence()
    {
        // Asks 2: identity binding is not opt-in. A foreign principal that simply stays silent must not
        // be able to actuate a grant sealed for somebody else.
        var operation = ProtectedRecord(actor: "ops-agent", tenantId: "tenant-a");

        RecoveryGrantFence.Evaluate(operation, new RollbackDeployOperationRequest { Reason = "silent" },
            authenticatedActor: "intruder", callerTenantId: "tenant-a", DateTimeOffset.UtcNow)
            !.Code.Should().Be(RecoveryGrantFence.ActorMismatchCode);

        RecoveryGrantFence.Evaluate(operation, new RollbackDeployOperationRequest { Reason = "silent" },
            authenticatedActor: "ops-agent", callerTenantId: "tenant-b", DateTimeOffset.UtcNow)
            !.Code.Should().Be(RecoveryGrantFence.TenantMismatchCode);

        RecoveryGrantFence.Evaluate(operation, new RollbackDeployOperationRequest { Reason = "silent" },
            authenticatedActor: "ops-agent", callerTenantId: "tenant-a", DateTimeOffset.UtcNow)
            .Should().BeNull("the recorded actor and tenant may actuate their own grant");

        RecoveryGrantFence.Evaluate(operation, new RollbackDeployOperationRequest { Reason = "silent" },
            authenticatedActor: "ops-agent", callerTenantId: null, DateTimeOffset.UtcNow)
            !.Code.Should().Be(RecoveryGrantFence.TenantMismatchCode, "an unbound caller is not the sealed tenant binding");
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void Fence_DeclaredIdentityIsBoundToTheSealedGrantNotOnlyToTheCaller()
    {
        // honua-server#4987 ask 1: every declared term but actor/tenant quotes tenant-a's grant; the body
        // names the caller's own identity. Checking only the caller admitted it.
        var operation = ProtectedRecord(actor: "ops-a", tenantId: "tenant-a");
        static RollbackDeployOperationRequest QuotedGrant(string actor, string? tenantId) => new()
        {
            Reason = "quoted grant",
            TargetId = TargetId,
            ExpectedCandidateRevision = "rev-candidate-3",
            ExpectedPreviousRevision = "rev-prior-1",
            ExpectedProtectionPhase = "observing",
            GrantId = "grant-abc",
            PolicyDigest = "DIGEST",
            Actor = actor,
            TenantId = tenantId,
            Compensation = DeployRecoveryCompensations.RestorePreviousRevision,
        };

        RecoveryGrantFence.Evaluate(operation, QuotedGrant("ops-b", "tenant-b"),
            authenticatedActor: "ops-b", callerTenantId: "tenant-b", DateTimeOffset.UtcNow)
            !.Code.Should().Be(RecoveryGrantFence.ActorMismatchCode);

        // The same actor name bound to another tenant is still not the sealed principal.
        RecoveryGrantFence.Evaluate(operation, QuotedGrant("ops-a", "tenant-b"),
            authenticatedActor: "ops-a", callerTenantId: "tenant-b", DateTimeOffset.UtcNow)
            !.Code.Should().Be(RecoveryGrantFence.TenantMismatchCode);

        // A grant sealed by a tenantless principal is not actuatable by a tenant-bound namesake.
        RecoveryGrantFence.Evaluate(ProtectedRecord(actor: "ops-a", tenantId: null), QuotedGrant("ops-a", null),
            authenticatedActor: "ops-a", callerTenantId: "tenant-b", DateTimeOffset.UtcNow)
            !.Code.Should().Be(RecoveryGrantFence.TenantMismatchCode);

        // A declared actor is refused against a grant that sealed no actor at all.
        RecoveryGrantFence.Evaluate(ProtectedRecord(actor: null, tenantId: "tenant-a"), QuotedGrant("ops-a", null),
            authenticatedActor: "ops-a", callerTenantId: "tenant-a", DateTimeOffset.UtcNow)
            !.Code.Should().Be(RecoveryGrantFence.ActorMismatchCode);

        RecoveryGrantFence.Evaluate(operation, QuotedGrant("ops-a", "tenant-a"),
            authenticatedActor: "ops-a", callerTenantId: "tenant-a", DateTimeOffset.UtcNow)
            .Should().BeNull("the sealed principal quoting its own grant is the one recovery preauthorized");
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
            authenticatedActor: "admin", callerTenantId: null, DateTimeOffset.UtcNow)
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

    private async Task<JsonElement> GetOperationAsync(string operationId, HttpClient? client = null)
    {
        var response = await (client ?? _client).GetAsync($"/api/v1/admin/deploy/operations/{operationId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private async Task<string> CreateSubmittedOperationAsync(string desiredRevision, HttpClient? client = null)
    {
        client ??= _client;
        var createResponse = await client.PostAsJsonAsync("/api/v1/admin/deploy/operations", new
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

        var submitResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/deploy/operations/{operationId}/submit", new { reason = "approved" });
        submitResponse.StatusCode.Should().Be(HttpStatusCode.OK, await submitResponse.Content.ReadAsStringAsync());
        return operationId;
    }

    private async Task<ProtectedActivation> CreateProtectedActivationAsync(HttpClient? client = null)
    {
        client ??= _client;
        var candidateRevision = $"rev-candidate-{Guid.NewGuid():N}";
        var operationId = await CreateSubmittedOperationAsync(candidateRevision, client);

        var promoteResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/deploy/operations/{operationId}/promote", new { reason = "cutover" });
        promoteResponse.StatusCode.Should().Be(HttpStatusCode.OK, await promoteResponse.Content.ReadAsStringAsync());

        var operation = await GetOperationAsync(operationId);
        var protection = operation.GetProperty("protection");
        protection.ValueKind.Should().Be(JsonValueKind.Object, "promotion opens a protection window");
        return new ProtectedActivation(operationId, candidateRevision, protection);
    }

    private HttpClient CreateBearerClient(FencePrincipal principal)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, principal.Subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("name", principal.Subject),
            new("tenant_id", principal.TenantId),
        };
        foreach (var role in principal.Roles)
        {
            claims.Add(new Claim("roles", role));
        }

        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
                SecurityAlgorithms.HmacSha256)));
        return _fixture.CreateClient(client =>
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token));
    }

    private sealed record FencePrincipal(string Subject, string TenantId, string[] Roles);

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

        public Task<WorkflowOperationPage> QueryAsync(WorkflowOperationQuery query, CancellationToken cancellationToken = default)
        {
            lock (_operations)
            {
                var matching = _operations.Values
                    .Where(op => (!query.Kind.HasValue || op.Kind == query.Kind.Value) &&
                        (!query.Status.HasValue || op.Status == query.Status.Value))
                    .OrderByDescending(op => op.CreatedAt)
                    .ToArray();
                var items = matching.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToArray();
                return Task.FromResult(new WorkflowOperationPage
                {
                    Items = items,
                    Page = query.Page,
                    PageSize = query.PageSize,
                    TotalCount = matching.Length,
                    HasMore = query.Page * query.PageSize < matching.Length,
                });
            }
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
