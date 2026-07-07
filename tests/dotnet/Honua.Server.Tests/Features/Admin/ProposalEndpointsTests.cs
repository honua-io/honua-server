// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ProposalEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IOperationProposalStore>();
                services.RemoveAll<IGuardrailLadder>();
                services.RemoveAll<IProposalNotifier>();
                services.RemoveAll<IOperationGateway>();
                services.RemoveAll<IOperationExecutor>();

                services.AddSingleton<IOperationProposalStore>(_proposalStore);
                services.AddSingleton<IGuardrailLadder>(_ladder);
                services.AddSingleton<IProposalNotifier>(_notifier);
                services.AddSingleton<IOperationExecutor>(_executor);
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
        string? requestedBy = "agent:test")
    {
        var now = DateTimeOffset.UtcNow;
        var proposal = new OperationProposal
        {
            ProposalId = $"proposal-{Guid.NewGuid():N}",
            Kind = OperationClass.AdminConfigChange,
            Status = status,
            RequestedBy = requestedBy,
            Plan = new OperationProposalPlan { Summary = "Change setting X", RiskLevel = ProposalRiskLevel.Medium },
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
    public async Task GetProposal_WhenMissing_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/v1/admin/proposals/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
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
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task ApproveProposal_BySameRequester_IsForbiddenForSeparationOfDuties()
    {
        // The admin client authenticates as the "admin" principal; seeding the
        // proposal with that same requester must trip the separation-of-duties guard.
        var proposal = await SeedProposalAsync(requestedBy: "admin");

        var response = await _client.PostAsync($"/api/v1/admin/proposals/{proposal.ProposalId}/approve", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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

    private sealed class RecordingExecutor : IOperationExecutor
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
