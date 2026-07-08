// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// API-surface integration tests for the platform-release converge endpoint (#2564). Converge is
/// exercised through the REAL <see cref="Honua.ControlPlane.OperationGateway"/> and
/// <see cref="Honua.ControlPlane.Executors.DeployOperationExecutor"/> — only the guardrail tier,
/// the proposal store, and the durable workflow store are stubbed so each routing path is
/// deterministic without an Enterprise license or Redis.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class PlatformReleaseConvergeEndpointTests : IAsyncLifetime
{
    private const string DeclaredVersion = "2026.07.0";
    private const string DeclaredServingArtifact = "ghcr.io/honua/server:2026.07.0";

    private readonly InMemoryWorkflowOperationStore _workflowStore = new();
    private readonly TestProposalStore _proposalStore = new();
    private readonly StubGuardrailLadder _ladder = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public PlatformReleaseConvergeEndpointTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                ConfigurePlatformRelease(services);

                services.RemoveAll<IWorkflowOperationStore>();
                services.RemoveAll<IWorkflowOperationReconciler>();
                services.RemoveAll<IOperationProposalStore>();
                services.RemoveAll<IGuardrailLadder>();
                services.RemoveAll<IProposalNotifier>();
                services.RemoveAll<IOperationExecutor>();
                services.RemoveAll<IOperationGateway>();

                services.AddSingleton<IWorkflowOperationStore>(_workflowStore);
                services.AddSingleton<IWorkflowOperationReconciler>(new StubWorkflowOperationReconciler());
                services.AddSingleton<IOperationProposalStore>(_proposalStore);
                services.AddSingleton<IGuardrailLadder>(_ladder);
                services.AddSingleton<IProposalNotifier>(new NoOpProposalNotifier());
                services.AddSingleton<IOperationExecutor, Honua.ControlPlane.Executors.DeployOperationExecutor>();
                services.AddSingleton<IOperationGateway, Honua.ControlPlane.OperationGateway>();
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/platform-release/converge")]
    public async Task Converge_ClassifiesEachTargetAgainstTheDivergenceContract()
    {
        _ladder.Tier = GuardrailTier.DirectExecute;

        // srv-converged is already at the declared release; srv-divergent last landed an older revision.
        SeedSucceededDeploy("srv-converged", DeclaredServingArtifact);
        SeedSucceededDeploy("srv-divergent", "ghcr.io/honua/server:2026.06.0");

        var response = await _client.PostAsJsonAsync("/api/v1/admin/platform-release/converge", new { });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("releaseVersion").GetString().Should().Be(DeclaredVersion);
        root.GetProperty("servingArtifactReference").GetString().Should().Be(DeclaredServingArtifact);
        root.GetProperty("workersDeferred").GetBoolean().Should().BeTrue();
        // Some targets diverged, so at least one operation was created => not fully converged.
        root.GetProperty("converged").GetBoolean().Should().BeFalse();

        var byTarget = root.GetProperty("targets").EnumerateArray()
            .ToDictionary(t => t.GetProperty("targetId").GetString()!, t => t);

        // No terminal-Succeeded op => unknown => treated as divergent, operation created.
        byTarget["srv-unknown"].GetProperty("outcome").GetString().Should().Be("unknown-treated-divergent");
        byTarget["srv-unknown"].GetProperty("operationId").GetString().Should().NotBeNullOrWhiteSpace();

        // Last-applied == declared => no-op, no operation created.
        byTarget["srv-converged"].GetProperty("outcome").GetString().Should().Be("already-converged");
        byTarget["srv-converged"].TryGetProperty("operationId", out _).Should().BeFalse();

        // Last-applied != declared => divergent, operation created.
        byTarget["srv-divergent"].GetProperty("outcome").GetString().Should().Be("operation-created");
        byTarget["srv-divergent"].GetProperty("operationId").GetString().Should().NotBeNullOrWhiteSpace();
        byTarget["srv-divergent"].GetProperty("lastAppliedRevision").GetString().Should().Be("ghcr.io/honua/server:2026.06.0");

        // Explicit pin diverging from the release => skipped (config-derived skew is not cleared at runtime).
        byTarget["srv-pinned"].GetProperty("outcome").GetString().Should().Be("skipped-pinned");
        byTarget["srv-pinned"].TryGetProperty("operationId", out _).Should().BeFalse();

        // No deploy operation is ever created for a converged or pinned target.
        var createdForConverged = _workflowStore.CreatedDeployTargets.Count(id => id == "srv-converged");
        createdForConverged.Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/platform-release/converge")]
    public async Task Converge_IsIdempotent_DoubleConvergeFoldsOntoOneOperation()
    {
        _ladder.Tier = GuardrailTier.DirectExecute;
        SeedSucceededDeploy("srv-divergent", "ghcr.io/honua/server:2026.06.0");

        var firstOperationId = await ConvergeAndReadTargetOperationId("srv-divergent");
        var secondOperationId = await ConvergeAndReadTargetOperationId("srv-divergent");

        firstOperationId.Should().NotBeNullOrWhiteSpace();
        secondOperationId.Should().Be(firstOperationId,
            "the converge:{version}:{targetId} idempotency key must fold a double-converge onto one operation");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/platform-release/converge")]
    public async Task Converge_RoutesThroughApprovalGateway_WithoutDirectExecuteBypass()
    {
        // The guardrail tier requires approval: converge must create a PROPOSAL, never execute directly.
        _ladder.Tier = GuardrailTier.RequiresApproval;
        SeedSucceededDeploy("srv-divergent", "ghcr.io/honua/server:2026.06.0");

        var response = await _client.PostAsJsonAsync("/api/v1/admin/platform-release/converge", new { });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var byTarget = document.RootElement.GetProperty("targets").EnumerateArray()
            .ToDictionary(t => t.GetProperty("targetId").GetString()!, t => t);

        var divergent = byTarget["srv-divergent"];
        divergent.GetProperty("outcome").GetString().Should().Be("operation-created");
        divergent.GetProperty("proposalId").GetString().Should().NotBeNullOrWhiteSpace();
        // No deploy operation was executed directly — the gateway parked it for approval.
        divergent.TryGetProperty("operationId", out var opId).Should().BeFalse();
        _ = opId;
        _workflowStore.CreatedDeployTargets.Should().NotContain("srv-divergent");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/platform-release/converge")]
    public async Task Converge_WhenNoReleaseDeclared_ReturnsBadRequest()
    {
        var noReleaseFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IWorkflowOperationStore>();
                services.RemoveAll<IWorkflowOperationReconciler>();
                services.AddSingleton<IWorkflowOperationStore>(new InMemoryWorkflowOperationStore());
                services.AddSingleton<IWorkflowOperationReconciler>(new StubWorkflowOperationReconciler());
            });

        try
        {
            await noReleaseFixture.InitializeAsync();
            var client = noReleaseFixture.CreateAdminClient();

            var response = await client.PostAsJsonAsync("/api/v1/admin/platform-release/converge", new { });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await noReleaseFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/platform-release/converge")]
    public async Task Converge_WithoutAdminAuthorization_IsRejected()
    {
        // Disable the dev-auth bypass so the admin authorization gate is actually enforced.
        var authFixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "converge-auth-test-key");
            });

        try
        {
            await authFixture.InitializeAsync();
            var anonymous = authFixture.CreateClient(); // No admin headers.

            var response = await anonymous.PostAsJsonAsync("/api/v1/admin/platform-release/converge", new { });

            response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        }
        finally
        {
            await authFixture.DisposeAsync();
        }
    }

    private async Task<string?> ConvergeAndReadTargetOperationId(string targetId)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/platform-release/converge", new { });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var target = document.RootElement.GetProperty("targets").EnumerateArray()
            .First(t => t.GetProperty("targetId").GetString() == targetId);
        return target.TryGetProperty("operationId", out var opId) ? opId.GetString() : null;
    }

    private void SeedSucceededDeploy(string targetId, string desiredRevision)
    {
        var now = DateTimeOffset.UtcNow;
        _workflowStore.Seed(new WorkflowOperationRecord
        {
            OperationId = $"deploy-seed-{targetId}-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Succeeded,
            CreatedAt = now.AddMinutes(-10),
            UpdatedAt = now.AddMinutes(-9),
            CompletedAt = now.AddMinutes(-9),
            Deploy = new DeployOperationSpec
            {
                TargetId = targetId,
                TargetKind = DeployTargetKind.Kubernetes,
                Backend = "honua-gitops-kubernetes",
                Environment = "production",
                TargetName = "honua-server",
                DesiredRevision = desiredRevision,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            }
        });
    }

    private static void ConfigurePlatformRelease(IServiceCollection services)
    {
        services.Configure<ControlPlaneOptions>(options =>
        {
            options.PlatformRelease = new PlatformReleaseOptions
            {
                Version = DeclaredVersion,
                ServingArtifactReference = DeclaredServingArtifact,
                Workers =
                [
                    new PlatformReleaseWorkerImageOptions { ArtifactReference = "ghcr.io/honua/worker:2026.07.0" }
                ]
            };

            options.DeployTargets =
            [
                NewTarget("srv-unknown", artifactReference: null),
                NewTarget("srv-converged", artifactReference: null),
                NewTarget("srv-divergent", artifactReference: null),
                // Explicit pin diverging from the declared serving artifact => skipped at runtime.
                NewTarget("srv-pinned", artifactReference: "ghcr.io/honua/server:custom-pin")
            ];
        });
    }

    private static DeployTargetOptions NewTarget(string targetId, string? artifactReference)
        => new()
        {
            TargetId = targetId,
            TargetKind = DeployTargetKind.Kubernetes,
            Backend = "honua-gitops-kubernetes",
            Environment = "production",
            TargetName = "honua-server",
            ArtifactReference = artifactReference
        };

    private sealed class StubGuardrailLadder : IGuardrailLadder
    {
        public GuardrailTier Tier { get; set; } = GuardrailTier.DirectExecute;

        public GuardrailDecision Resolve(OperationClass operationClass)
            => Resolve(operationClass, Honua.Core.Features.Licensing.Domain.HonuaEdition.Community);

        public GuardrailDecision Resolve(
            OperationClass operationClass,
            Honua.Core.Features.Licensing.Domain.HonuaEdition edition)
            => new(Tier, operationClass, edition, "test-stub");

        public GuardrailDecision Resolve(OperationClass operationClass, string? actionDiscriminator)
            => Resolve(operationClass, actionDiscriminator, Honua.Core.Features.Licensing.Domain.HonuaEdition.Community);

        public GuardrailDecision Resolve(
            OperationClass operationClass,
            string? actionDiscriminator,
            Honua.Core.Features.Licensing.Domain.HonuaEdition edition)
            => new(Tier, operationClass, edition, "test-stub");
    }

    private sealed class NoOpProposalNotifier : IProposalNotifier
    {
        public Task NotifyPendingAsync(OperationProposal proposal, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyResolvedAsync(OperationProposal proposal, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubWorkflowOperationReconciler : IWorkflowOperationReconciler
    {
        public Task ReconcileWorkflowOperationAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
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

    private sealed class InMemoryWorkflowOperationStore : IWorkflowOperationStore
    {
        private readonly Dictionary<string, WorkflowOperationRecord> _operations = new(StringComparer.Ordinal);

        /// <summary>Target ids for which a NON-seeded deploy operation was durably created.</summary>
        public List<string> CreatedDeployTargets { get; } = [];

        /// <summary>Seeds a pre-existing operation without recording it as a converge-created deploy.</summary>
        public void Seed(WorkflowOperationRecord operation) => _operations[operation.OperationId] = operation;

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
            if (operation.Kind == WorkflowOperationKind.Deploy && operation.Deploy is not null)
            {
                CreatedDeployTargets.Add(operation.Deploy.TargetId);
            }

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
        {
            var operations = _operations.Values
                .Where(operation => !kind.HasValue || operation.Kind == kind.Value)
                .ToArray();

            return Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(operations);
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
    }
}
