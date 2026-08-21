// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Operations.Admin;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Honua.Infrastructure.Security;
using CatalogOperationExecutor = Honua.Core.Features.Operations.Abstractions.IOperationExecutor;
using ControlPlaneOperationExecutor = Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor;
using AdminProposalEndpoints = Honua.Server.Features.Admin.ProposalEndpoints;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// API-surface integration tests for the console approval REST API and the shared
/// operation gateway (#1693/#1694/#1695). The guardrail ladder is stubbed so each
/// tier can be exercised deterministically without an Enterprise license.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ApprovalManagement)]
public sealed class ProposalEndpointsTests : IAsyncLifetime
{
    private readonly TestProposalStore _proposalStore = new();
    private readonly StubGuardrailLadder _ladder = new();
    private readonly RecordingProposalNotifier _notifier = new();
    private readonly RecordingExecutor _executor = new();
    private readonly RecordingPublishedOperationExecutor _publishedExecutor = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ProposalEndpointsTests()
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
                services.RemoveAll<IOperationProposalStore>();
                services.RemoveAll<IGuardrailLadder>();
                services.RemoveAll<IProposalNotifier>();
                services.RemoveAll<IOperationGateway>();
                services.RemoveAll<ControlPlaneOperationExecutor>();
                services.RemoveAll<CatalogOperationExecutor>();
                services.RemoveAll<Honua.Core.Features.Operations.Abstractions.IOperationApprovalProposalBridge>();

                services.AddSingleton<IOperationProposalStore>(_proposalStore);
                services.AddSingleton<IGuardrailLadder>(_ladder);
                services.AddSingleton<IProposalNotifier>(_notifier);
                services.AddSingleton<ControlPlaneOperationExecutor>(_executor);
                services.AddSingleton<ControlPlaneOperationExecutor, PublishedOperationControlPlaneExecutor>();
                services.AddSingleton<CatalogOperationExecutor>(_publishedExecutor);
                services.AddScoped<ApprovedAdminOperationRunner>();
                services.AddScoped<Honua.Core.Features.Operations.Abstractions.IOperationApprovalProposalBridge,
                    AdminOperationApprovalBridge>();
                services.AddSingleton<IOperationGateway, Honua.ControlPlane.OperationGateway>();
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private async Task<OperationProposal> SeedProposalAsync(
        OperationProposalStatus status = OperationProposalStatus.AwaitingApproval,
        string? requestedBy = "agent:test",
        OperationProposalAutonomyMetadata? autonomyMetadata = null,
        string? executionPayload = null)
    {
        var now = DateTimeOffset.UtcNow;
        var proposal = new OperationProposal
        {
            ProposalId = $"proposal-{Guid.NewGuid():N}",
            Kind = OperationClass.AdminConfigChange,
            Status = status,
            RequestedBy = requestedBy,
            AutonomyMetadata = autonomyMetadata,
            Plan = new OperationProposalPlan
            {
                Summary = "Change setting X",
                RiskLevel = ProposalRiskLevel.Medium,
                ExecutionPayload = executionPayload,
            },
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _proposalStore.TryCreateAsync(proposal);
        return proposal;
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/proposals")]
    public async Task ListProposals_ReturnsActiveProposals()
    {
        await SeedProposalAsync();

        var response = await _client.GetAsync("/api/v1/admin/proposals");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("proposals").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/proposals/{id}")]
    public async Task GetProposal_WhenFound_ReturnsPlanDetail()
    {
        var proposal = await SeedProposalAsync();

        var response = await _client.GetAsync($"/api/v1/admin/proposals/{proposal.ProposalId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("proposalId").GetString().Should().Be(proposal.ProposalId);
        document.RootElement.GetProperty("summary").GetString().Should().Be("Change setting X");
        document.RootElement.GetProperty("riskLevel").GetString().Should().Be("Medium");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/proposals/{id}")]
    public async Task GetProposal_FindingMetadata_ReturnsStableLinkWithoutExecutionPayload()
    {
        var proposal = await SeedProposalAsync(
            autonomyMetadata: new OperationProposalAutonomyMetadata
            {
                FindingId = "finding-1",
                Rule = "alert-dispatch-backlog",
                ActionDiscriminator = "alerts.redrive_dead_letters",
                ActionMarkedAutoSafe = true,
            },
            executionPayload: "{\"password\":\"must-not-leak\"}");

        var response = await _client.GetAsync($"/api/v1/admin/proposals/{proposal.ProposalId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("findingId").GetString().Should().Be("finding-1");
        document.RootElement.GetProperty("autonomyRule").GetString().Should().Be("alert-dispatch-backlog");
        document.RootElement.GetProperty("actionDiscriminator").GetString().Should().Be("alerts.redrive_dead_letters");
        json.Contains("executionPayload", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        json.Contains("must-not-leak", StringComparison.Ordinal).Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/proposals/{id}")]
    public async Task GetProposal_WhenMissing_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/v1/admin/proposals/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ApproveProposal_HappyPath_ExecutesAndMarksSubmitted()
    {
        // Requester differs from the admin approver so separation-of-duties passes.
        var proposal = await SeedProposalAsync(requestedBy: "agent:proposer");

        var response = await _client.PostAsync($"/api/v1/admin/proposals/{proposal.ProposalId}/approve", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetString().Should().Be("Submitted");
        _executor.Executed.Should().BeTrue();
        _notifier.ResolvedCount.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ApproveProposal_AdminApproveKey_CanReadAndApproveButCannotWriteElsewhere()
    {
        var proposal = await SeedProposalAsync(requestedBy: "agent:proposer");
        var (keyId, key) = await CreateScopedApiKeyAsync("focused-console", ["admin:read", "admin:approve"]);
        using var focusedConsole = _fixture.CreateClient(
            client => client.DefaultRequestHeaders.Add("X-API-Key", key));

        using var listResponse = await focusedConsole.GetAsync("/api/v1/admin/proposals");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var approveResponse = await focusedConsole.PostAsync(
            $"/api/v1/admin/proposals/{proposal.ProposalId}/approve",
            null);
        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var writeResponse = await focusedConsole.PostAsJsonAsync(
            "/api/v1/admin/api-keys",
            new { name = "must-not-create", permissions = new[] { "admin:read" } });
        writeResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var effectiveResponse = await _client.GetAsync(
            $"/api/v1/admin/api-keys/{keyId}/effective-permissions");
        effectiveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var effectiveDocument = JsonDocument.Parse(await effectiveResponse.Content.ReadAsStringAsync());
        effectiveDocument.RootElement.GetProperty("data").GetProperty("permissions")
            .EnumerateArray().Select(value => value.GetString())
            .Should().Equal("admin:read", "admin:approve");
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ApproveProposal_ReadOnlyKey_ReturnsProblemNamingMissingGrant()
    {
        var proposal = await SeedProposalAsync(requestedBy: "agent:proposer");
        var (_, key) = await CreateScopedApiKeyAsync("console-read-only", ["admin:read"]);
        using var readOnlyConsole = _fixture.CreateClient(
            client => client.DefaultRequestHeaders.Add("X-API-Key", key));

        using var response = await readOnlyConsole.PostAsync(
            $"/api/v1/admin/proposals/{proposal.ProposalId}/approve",
            null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync()).Should().Contain("admin:approve");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task ApproveProposal_BySameRequester_IsForbiddenForSeparationOfDuties()
    {
        // The admin client authenticates as the scheme-qualified bootstrap principal; seeding the
        // proposal with that same requester must trip the separation-of-duties guard.
        var proposal = await SeedProposalAsync(requestedBy: "admin:bootstrap");

        var response = await _client.PostAsync($"/api/v1/admin/proposals/{proposal.ProposalId}/approve", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [UnitTest]
    public async Task SeparationOfDuties_OidcDisplayNameMismatch_StillDeniesSelfApproval()
    {
        var proposer = OidcPrincipal("subject-1", displayName: "Name Before");
        var approver = OidcPrincipal("subject-1", displayName: "Name After");
        var proposerActor = CanonicalSecurityActor.Resolve(proposer)!.ActorId;
        var proposal = await SeedProposalAsync(requestedBy: proposerActor);
        var resolver = Substitute.For<IPermissionResolver>();
        resolver.AuthorizeAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<AuthorizationOperation>(),
                true,
                Arg.Any<CancellationToken>())
            .Returns(PermissionDecision.NoMatch());
        var context = new DefaultHttpContext { User = approver };

        var denied = await AdminProposalEndpoints.EnsureApproverAsync(
            resolver,
            _proposalStore,
            proposal.ProposalId,
            CanonicalSecurityActor.Resolve(approver)!.ActorId,
            context);

        denied.Should().NotBeNull("display names cannot change the immutable OIDC actor id");
    }

    [UnitTest]
    public async Task SeparationOfDuties_NamelessOidcSub_StillDeniesSelfApproval()
    {
        var principal = OidcPrincipal("subject-without-name", displayName: null);
        var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
        var proposal = await SeedProposalAsync(requestedBy: actor);
        var resolver = Substitute.For<IPermissionResolver>();
        resolver.AuthorizeAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<AuthorizationOperation>(),
                true,
                Arg.Any<CancellationToken>())
            .Returns(PermissionDecision.NoMatch());
        var context = new DefaultHttpContext { User = principal };

        var denied = await AdminProposalEndpoints.EnsureApproverAsync(
            resolver,
            _proposalStore,
            proposal.ProposalId,
            actor,
            context);

        denied.Should().NotBeNull("sub is a durable identity even when Identity.Name is null");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task ApproveProposal_AlreadyResolved_ReturnsConflict()
    {
        var proposal = await SeedProposalAsync(status: OperationProposalStatus.Rejected, requestedBy: "agent:proposer");

        var response = await _client.PostAsync($"/api/v1/admin/proposals/{proposal.ProposalId}/approve", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/reject")]
    public async Task RejectProposal_WithReason_MarksRejected()
    {
        var proposal = await SeedProposalAsync(requestedBy: "agent:proposer");

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/proposals/{proposal.ProposalId}/reject",
            new { reason = "not safe right now" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetString().Should().Be("Rejected");
        document.RootElement.GetProperty("resolutionReason").GetString().Should().Be("not safe right now");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/reject")]
    public async Task RejectProposal_WithoutReason_ReturnsBadRequest()
    {
        var proposal = await SeedProposalAsync(requestedBy: "agent:proposer");

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/proposals/{proposal.ProposalId}/reject",
            new { reason = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task Gateway_RequiresApprovalTier_PersistsProposalAndNotifies()
    {
        _ladder.Tier = GuardrailTier.RequiresApproval;
        var gateway = _fixture.Services.GetRequiredService<IOperationGateway>();

        var result = await gateway.RouteAsync(new OperationGatewayRequest
        {
            Kind = OperationClass.AdminConfigChange,
            RequestedBy = "agent:proposer",
            Reason = "tighten setting"
        });

        result.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated);
        result.ProposalId.Should().NotBeNullOrEmpty();
        _notifier.PendingCount.Should().BeGreaterThan(0);
        (await _proposalStore.GetAsync(result.ProposalId!)).Should().NotBeNull();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task Gateway_DirectExecuteTier_ExecutesImmediately()
    {
        _ladder.Tier = GuardrailTier.DirectExecute;
        var gateway = _fixture.Services.GetRequiredService<IOperationGateway>();

        var result = await gateway.RouteAsync(new OperationGatewayRequest
        {
            Kind = OperationClass.AdminConfigChange,
            RequestedBy = "agent:proposer"
        });

        result.Outcome.Should().Be(OperationGatewayOutcome.Executed);
        _executor.Executed.Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task Gateway_BlockedTier_ReturnsBlocked()
    {
        _ladder.Tier = GuardrailTier.Blocked;
        var gateway = _fixture.Services.GetRequiredService<IOperationGateway>();

        var result = await gateway.RouteAsync(new OperationGatewayRequest
        {
            Kind = OperationClass.AdminConfigChange,
            RequestedBy = "agent:proposer"
        });

        result.Outcome.Should().Be(OperationGatewayOutcome.Blocked);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task PublishedOperation_RequiresApproval_PersistsAndResumesExactCatalogOperation()
    {
        var (keyId, _) = await CreateScopedApiKeyAsync("proposal-originator", ["admin:read"]);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var bridge = scope.ServiceProvider
            .GetRequiredService<Honua.Core.Features.Operations.Abstractions.IOperationApprovalProposalBridge>();
        var request = new OperationRequest
        {
            OperationId = "admin.server.status",
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["includeCapabilities"] = "true",
            },
            IdempotencyKey = "published-operation-approval-test",
        };
        var operationContext = new OperationPolicyContext
        {
            ResolvedConnectionString = "Host=db;Password=must-not-be-persisted",
            PrincipalId = $"admin-api-key:api-key:{keyId:D}",
            AuthenticationScheme = "admin-api-key",
            ApiKeyId = keyId.ToString("D"),
            CorrelationId = "correlation-1",
            Roles = ["admin"],
            Permissions = ["admin:read"],
        };
        var descriptor = new OperationDescriptor
        {
            OperationId = request.OperationId,
            ProviderId = "test",
            Title = "Get server status",
            Description = "Test descriptor for the approval bridge.",
            Category = "admin",
            ExecutionKind = OperationExecutionKind.Synchronous,
            ApprovalModel = OperationApprovalModel.OperatorGate,
            Policy = new OperationPolicyMetadata
            {
                BlastRadiusClass = OperationBlastRadiusClass.None,
                SideEffectClass = OperationSideEffectClass.ReadOnly,
                Determinism = OperationDeterminism.Deterministic,
                SupportsDryRun = false,
                IsIdempotent = true,
            },
        };

        var handle = await bridge.CreateProposalAsync(
            descriptor,
            request,
            operationContext,
            new PolicyDecision
            {
                Kind = PolicyDecisionKind.RequireApproval,
                ApprovalLane = "admin-operator",
                Reason = "operator review",
            });

        handle.Status.Should().Be(OperationHandleStatus.RequiresApproval);
        handle.HandleId.Should().StartWith("proposal-");
        var persisted = await _proposalStore.GetAsync(handle.HandleId);
        persisted.Should().NotBeNull();
        persisted!.Kind.Should().Be(OperationClass.PublishedOperation);
        persisted.Plan.ExecutionPayload.Should().Contain(request.OperationId);
        persisted.Plan.ExecutionPayload.Should().NotContain("must-not-be-persisted");

        var listResponse = await _client.GetAsync("/api/v1/admin/proposals?kind=PublishedOperation");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await listResponse.Content.ReadAsStringAsync()).Should().Contain(handle.HandleId);

        var approvalResponse = await _client.PostAsync(
            $"/api/v1/admin/proposals/{handle.HandleId}/approve",
            null);

        approvalResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await approvalResponse.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetString().Should().Be("Submitted");
        _publishedExecutor.ExecutedRequest.Should().BeEquivalentTo(request);
        _publishedExecutor.ExecutedContext.Should().BeEquivalentTo(
            operationContext with { ResolvedConnectionString = null });
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task PublishedOperation_RawCredential_IsDeniedBeforeProposalPersistence()
    {
        var (keyId, _) = await CreateScopedApiKeyAsync("raw-secret-originator", ["admin:read"]);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var bridge = scope.ServiceProvider
            .GetRequiredService<Honua.Core.Features.Operations.Abstractions.IOperationApprovalProposalBridge>();
        var before = await _proposalStore.ListActiveAsync(OperationClass.PublishedOperation);

        var handle = await bridge.CreateProposalAsync(
            TestPublishedDescriptor(),
            new OperationRequest
            {
                OperationId = "admin.server.status",
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["body"] = "{\"password\":\"must-never-be-stored\"}",
                },
            },
            ApiKeyContext(keyId, ["admin:read"]),
            ApprovalDecision());

        handle.Status.Should().Be(OperationHandleStatus.Denied);
        handle.Reason.Should().Contain("Raw credentials");
        (await _proposalStore.ListActiveAsync(OperationClass.PublishedOperation))
            .Should().HaveCount(before.Count, "credential-bearing requests must never reach proposal storage");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task PublishedOperation_RevokedProposerKey_CannotResumeAfterApproval()
    {
        var (keyId, _) = await CreateScopedApiKeyAsync("revoked-originator", ["admin:read"]);
        var proposalId = await CreatePublishedProposalAsync(ApiKeyContext(keyId, ["admin:read"]));

        using var revoke = await _client.PostAsync($"/api/v1/admin/api-keys/{keyId:D}/revoke", null);
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);
        using var approval = await _client.PostAsync($"/api/v1/admin/proposals/{proposalId}/approve", null);

        approval.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await approval.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetString().Should().Be("Failed");
        _publishedExecutor.ExecutedRequest.Should().BeNull("a revoked proposer key must deny resume");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task PublishedOperation_DowngradedOidcProposer_CannotResumeAfterApproval()
    {
        const string subject = "oidc-subject-1";
        const string issuer = "https://issuer.example.com";
        var userStore = _fixture.Services.GetRequiredService<IUserStore>();
        var scimStore = _fixture.Services.GetRequiredService<IScimUserStore>();
        await scimStore.CreateUserAsync(new ScimUserProvisioning
        {
            UserName = "oidc-user-1",
            ExternalId = subject,
            ExternalIssuer = issuer,
            DisplayName = "Changed Display Name",
            Roles = ["publisher"],
        });
        var context = new OperationPolicyContext
        {
            PrincipalId = $"oidc:subject:{Uri.EscapeDataString(issuer)}:{subject}",
            AuthenticationScheme = "oidc",
            SubjectId = subject,
            SubjectIssuer = issuer,
            Roles = ["publisher"],
        };
        var proposalId = await CreatePublishedProposalAsync(context);

        await userStore.UpdateUserRolesAsync("oidc-user-1", ["viewer"]);
        using var approval = await _client.PostAsync($"/api/v1/admin/proposals/{proposalId}/approve", null);

        approval.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await approval.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetString().Should().Be("Failed");
        _publishedExecutor.ExecutedRequest.Should().BeNull("a downgraded proposer must deny resume");
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task PublishedOperation_OperatorBearerProposerPreservesIssuerAndCannotResumeAfterRoleRevocation()
    {
        const string subject = "operator-bearer-subject-1";
        const string issuer = "https://issuer-a.example.com";
        var userStore = _fixture.Services.GetRequiredService<IUserStore>();
        var scimStore = _fixture.Services.GetRequiredService<IScimUserStore>();
        await scimStore.CreateUserAsync(new ScimUserProvisioning
        {
            UserName = "operator-bearer-user-1",
            ExternalId = subject,
            ExternalIssuer = issuer,
            DisplayName = "Operator Bearer User",
            Roles = ["publisher"],
        });

        var tokenService = new OperatorBearerTokenService(Options.Create(new OperatorBearerOptions
        {
            Enabled = true,
            SigningKey = "operator-bearer-proposal-test-key-at-least-32-bytes-long",
            Issuer = "honua-operator-bearer",
            Audience = "honua-admin-api",
            MaxLifetimeMinutes = 10,
        }));
        var issuance = tokenService.Issue(
        [
            new AdminAuthSessionClaim { Type = ClaimTypes.NameIdentifier, Value = subject },
            new AdminAuthSessionClaim { Type = "sub", Value = subject },
            new AdminAuthSessionClaim { Type = "iss", Value = issuer },
            new AdminAuthSessionClaim { Type = "auth_type", Value = "oidc" },
            new AdminAuthSessionClaim { Type = ClaimTypes.Role, Value = "publisher" },
        ],
        DateTimeOffset.UtcNow.AddMinutes(10));
        var validatedClaims = await tokenService.TryValidateAsync(issuance!.Token);
        var principal = AdminAuthClaimsProjector.CreatePrincipal(
            validatedClaims!,
            "OperatorBearer",
            "operator-bearer");
        var actor = CanonicalSecurityActor.Resolve(principal);
        actor.Should().NotBeNull();
        actor!.SubjectIssuer.Should().Be(issuer, "the wrapper issuer is never the upstream identity namespace");

        var proposalId = await CreatePublishedProposalAsync(new OperationPolicyContext
        {
            PrincipalId = actor.ActorId,
            AuthenticationScheme = actor.AuthenticationScheme,
            SubjectId = actor.SubjectId,
            SubjectIssuer = actor.SubjectIssuer,
            Roles = ["publisher"],
        });

        await userStore.UpdateUserRolesAsync("operator-bearer-user-1", ["viewer"]);
        using var approval = await _client.PostAsync($"/api/v1/admin/proposals/{proposalId}/approve", null);

        approval.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await approval.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetString().Should().Be("Failed");
        _publishedExecutor.ExecutedRequest.Should().BeNull(
            "the original upstream OIDC membership must be revalidated after operator-bearer wrapping");
    }

    private async Task<string> CreatePublishedProposalAsync(OperationPolicyContext context)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var bridge = scope.ServiceProvider
            .GetRequiredService<Honua.Core.Features.Operations.Abstractions.IOperationApprovalProposalBridge>();
        var handle = await bridge.CreateProposalAsync(
            TestPublishedDescriptor(),
            new OperationRequest { OperationId = "admin.server.status" },
            context,
            ApprovalDecision());
        handle.Status.Should().Be(OperationHandleStatus.RequiresApproval);
        return handle.HandleId;
    }

    private static OperationPolicyContext ApiKeyContext(Guid keyId, IReadOnlyList<string> permissions) => new()
    {
        PrincipalId = $"admin-api-key:api-key:{keyId:D}",
        AuthenticationScheme = "admin-api-key",
        ApiKeyId = keyId.ToString("D"),
        Roles = ["admin"],
        Permissions = permissions,
    };

    private static PolicyDecision ApprovalDecision() => new()
    {
        Kind = PolicyDecisionKind.RequireApproval,
        ApprovalLane = "admin-operator",
        Reason = "operator review",
    };

    private static OperationDescriptor TestPublishedDescriptor() => new()
    {
        OperationId = "admin.server.status",
        ProviderId = "test",
        Title = "Get server status",
        Description = "Test descriptor for the approval bridge.",
        Category = "admin",
        ExecutionKind = OperationExecutionKind.Synchronous,
        ApprovalModel = OperationApprovalModel.OperatorGate,
        Policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.None,
            SideEffectClass = OperationSideEffectClass.ReadOnly,
            Determinism = OperationDeterminism.Deterministic,
            SupportsDryRun = false,
            IsIdempotent = true,
        },
    };

    private async Task<(Guid Id, string Key)> CreateScopedApiKeyAsync(
        string name,
        IReadOnlyList<string> permissions)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/api-keys",
            new { name, permissions });
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        return (
            data.GetProperty("apiKey").GetProperty("id").GetGuid(),
            data.GetProperty("key").GetString()!);
    }

    private static ClaimsPrincipal OidcPrincipal(string subject, string? displayName)
    {
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("iss", "https://issuer.example.com"),
            new("auth_type", "oidc"),
            new(ClaimTypes.Role, "admin"),
        };
        if (displayName is not null)
        {
            claims.Add(new Claim(ClaimTypes.Name, displayName));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "oidc"));
    }

    private sealed class StubGuardrailLadder : IGuardrailLadder
    {
        public GuardrailTier Tier { get; set; } = GuardrailTier.RequiresApproval;

        public GuardrailDecision Resolve(OperationClass operationClass)
            => Resolve(operationClass, Honua.Core.Features.Licensing.Domain.HonuaEdition.Enterprise);

        public GuardrailDecision Resolve(
            OperationClass operationClass,
            Honua.Core.Features.Licensing.Domain.HonuaEdition edition)
            => new(Tier, operationClass, edition, "test-stub");

        public GuardrailDecision Resolve(OperationClass operationClass, string? actionDiscriminator)
            => Resolve(operationClass, actionDiscriminator, Honua.Core.Features.Licensing.Domain.HonuaEdition.Enterprise);

        public GuardrailDecision Resolve(
            OperationClass operationClass,
            string? actionDiscriminator,
            Honua.Core.Features.Licensing.Domain.HonuaEdition edition)
            => new(Tier, operationClass, edition, "test-stub");
    }

    private sealed class RecordingExecutor : ControlPlaneOperationExecutor
    {
        public bool Executed { get; private set; }

        public OperationClass OperationClass => OperationClass.AdminConfigChange;

        public Task<OperationProposalPlan?> PlanAsync(
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposalPlan?>(new OperationProposalPlan { Summary = "stub plan" });

        public Task<string?> ExecuteAsync(
            OperationGatewayRequest request,
            string? executionPayload,
            CancellationToken cancellationToken = default)
        {
            Executed = true;
            return Task.FromResult<string?>("exec-1");
        }
    }

    private sealed class RecordingPublishedOperationExecutor : CatalogOperationExecutor
    {
        public string OperationId => "admin.server.status";

        public OperationRequest? ExecutedRequest { get; private set; }

        public OperationPolicyContext? ExecutedContext { get; private set; }

        public Task<OperationValidation> ValidateAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            ExecutedRequest = request;
            ExecutedContext = context;
            return Task.FromResult(new OperationHandle
            {
                OperationId = request.OperationId,
                HandleId = "published-operation-execution",
                Status = OperationHandleStatus.Completed,
                Result = new OperationResultSummary { Summary = "executed" },
            });
        }

        public Task<OperationStatus> GetStatusAsync(
            OperationHandle handle,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationStatus
            {
                OperationId = handle.OperationId,
                HandleId = handle.HandleId,
                Status = handle.Status,
                Result = handle.Result,
            });
    }

    private sealed class RecordingProposalNotifier : IProposalNotifier
    {
        public int PendingCount { get; private set; }

        public int ResolvedCount { get; private set; }

        public Task NotifyPendingAsync(OperationProposal proposal, CancellationToken cancellationToken = default)
        {
            PendingCount++;
            return Task.CompletedTask;
        }

        public Task NotifyResolvedAsync(OperationProposal proposal, CancellationToken cancellationToken = default)
        {
            ResolvedCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TestProposalStore : IOperationProposalStore
    {
        private readonly Dictionary<string, OperationProposal> _proposals = new(StringComparer.Ordinal);

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCreateAsync(OperationProposal proposal, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            if (_proposals.ContainsKey(proposal.ProposalId))
            {
                return Task.FromResult(false);
            }

            _proposals[proposal.ProposalId] = proposal with { Version = 0 };
            return Task.FromResult(true);
        }

        public Task<OperationProposal?> GetAsync(string proposalId, CancellationToken cancellationToken = default)
            => Task.FromResult(_proposals.TryGetValue(proposalId, out var proposal) ? proposal : null);

        public Task<bool> TrySetAsync(OperationProposal proposal, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            if (_proposals.TryGetValue(proposal.ProposalId, out var existing) && existing.Version != proposal.Version)
            {
                return Task.FromResult(false);
            }

            _proposals[proposal.ProposalId] = proposal with { Version = proposal.Version + 1 };
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<OperationProposal>> ListActiveAsync(OperationClass? kind = null, CancellationToken cancellationToken = default)
        {
            var active = _proposals.Values
                .Where(proposal => !kind.HasValue || proposal.Kind == kind.Value)
                .Where(proposal => proposal.Status is not (OperationProposalStatus.Succeeded
                    or OperationProposalStatus.Failed
                    or OperationProposalStatus.Rejected
                    or OperationProposalStatus.RolledBack))
                .ToArray();

            return Task.FromResult<IReadOnlyList<OperationProposal>>(active);
        }
    }
}
