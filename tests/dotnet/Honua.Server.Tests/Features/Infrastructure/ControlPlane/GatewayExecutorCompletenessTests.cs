// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Coverage for honua-server#2563 (gateway executor completeness): the
/// <see cref="MetadataReleaseOperationExecutor"/> adapts
/// <see cref="MetadataReleaseControlService.CreateAsync"/> ONLY, Seed stays genuinely
/// unregistered (NotSupported, not a fake success), and <see cref="IOperationExecutorCatalog"/>
/// truthfully reports the registered set so discovery consumers never hit a dead end.
/// </summary>
public sealed class GatewayExecutorCompletenessTests
{
    // ── MetadataReleaseOperationExecutor: payload validation ───────────────────

    [UnitTest]
    public async Task PlanAsync_WithValidCreatePayload_ReturnsPlanWithDiff()
    {
        var executor = new MetadataReleaseOperationExecutor(new MetadataReleaseControlService([new InMemoryWorkflowOperationStore()]));
        var request = new OperationGatewayRequest
        {
            Kind = OperationClass.MetadataRelease,
            ExecutionPayload = CreatePayloadJson(packageId: "pkg-1", resourceSemanticId: "parcels", newFieldName: "owner_email"),
        };

        var plan = await executor.PlanAsync(request);

        plan.Should().NotBeNull();
        plan!.BlockingReasons.Should().BeEmpty();
        plan.Diff.Should().Contain(line => line.Contains("parcels"));
        plan.Diff.Should().Contain(line => line.Contains("owner_email"));
    }

    [UnitTest]
    public async Task PlanAsync_WithNonCreateAction_ReturnsBlockingPlan()
    {
        var executor = new MetadataReleaseOperationExecutor(new MetadataReleaseControlService([new InMemoryWorkflowOperationStore()]));
        var request = new OperationGatewayRequest
        {
            Kind = OperationClass.MetadataRelease,
            ExecutionPayload = CreatePayloadJson(action: "rollback"),
        };

        var plan = await executor.PlanAsync(request);

        plan.Should().NotBeNull();
        plan!.BlockingReasons.Should().ContainMatch("*Unsupported metadata release action*",
            "rollback must be rejected — it stays endpoint/cockpit-driven, not routed through the gateway (#2563)");
    }

    [UnitTest]
    public async Task ExecuteAsync_WithNonCreateAction_Throws()
    {
        var executor = new MetadataReleaseOperationExecutor(new MetadataReleaseControlService([new InMemoryWorkflowOperationStore()]));

        var act = async () => await executor.ExecuteAsync(
            new OperationGatewayRequest { Kind = OperationClass.MetadataRelease },
            CreatePayloadJson(action: "rollback"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Unsupported metadata release action*");
    }

    [UnitTest]
    public async Task ExecuteAsync_WithMissingJsonKeys_ThrowsMalformedPayload()
    {
        // Required members absent entirely trips System.Text.Json's own required-member
        // enforcement during deserialization (caught and reported as malformed payload).
        var executor = new MetadataReleaseOperationExecutor(new MetadataReleaseControlService([new InMemoryWorkflowOperationStore()]));

        var act = async () => await executor.ExecuteAsync(
            new OperationGatewayRequest { Kind = OperationClass.MetadataRelease },
            executionPayload: """{"action":"create","packageId":"pkg-1"}""");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not valid JSON*");
    }

    [UnitTest]
    public async Task ExecuteAsync_WithBlankRequiredFields_ThrowsRequiredFieldsMessage()
    {
        // Keys present but blank pass System.Text.Json's required-member check, so this
        // exercises the executor's own field-level validation message.
        var executor = new MetadataReleaseOperationExecutor(new MetadataReleaseControlService([new InMemoryWorkflowOperationStore()]));

        var act = async () => await executor.ExecuteAsync(
            new OperationGatewayRequest { Kind = OperationClass.MetadataRelease },
            executionPayload: """{"action":"create","packageId":"","targetEnvironment":"","resourceSemanticId":"","newFieldName":""}""");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*required*");
    }

    [UnitTest]
    public async Task ExecuteAsync_WithValidCreatePayload_CreatesReleaseOperation()
    {
        var store = new InMemoryWorkflowOperationStore();
        var executor = new MetadataReleaseOperationExecutor(new MetadataReleaseControlService([store]));
        var request = new OperationGatewayRequest
        {
            Kind = OperationClass.MetadataRelease,
            RequestedBy = "agent:tester",
            Reason = "additive evolution",
            ExecutionPayload = CreatePayloadJson(packageId: "pkg-2", resourceSemanticId: "parcels", newFieldName: "owner_phone", newFieldType: "String"),
        };

        var operationId = await executor.ExecuteAsync(request, request.ExecutionPayload);

        operationId.Should().NotBeNullOrEmpty();
        var stored = await store.GetAsync(operationId!);
        stored.Should().NotBeNull();
        stored!.Kind.Should().Be(WorkflowOperationKind.MetadataRelease);
        stored.Status.Should().Be(WorkflowOperationStatus.Submitted);
        stored.MetadataRelease!.ExecutionPlan!.NewFieldName.Should().Be("owner_phone");
        stored.MetadataRelease.ExecutionPlan.ResourceSemanticId.Should().Be("parcels");
        stored.Audit.RequestedBy.Should().Be("agent:tester");
    }

    // ── End-to-end through the real OperationGateway ───────────────────────────

    [UnitTest]
    public async Task Gateway_DirectExecuteTier_RoutesMetadataReleaseCreate_EndToEnd()
    {
        var workflowStore = new InMemoryWorkflowOperationStore();
        var executor = new MetadataReleaseOperationExecutor(new MetadataReleaseControlService([workflowStore]));
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.MetadataRelease).Returns(
            new GuardrailDecision(GuardrailTier.DirectExecute, OperationClass.MetadataRelease, HonuaEdition.Pro, "test"));

        var gateway = BuildGateway(new InMemoryProposalStore(), executor, ladder);

        var result = await gateway.RouteAsync(new OperationGatewayRequest
        {
            Kind = OperationClass.MetadataRelease,
            RequestedBy = "agent:tester",
            ExecutionPayload = CreatePayloadJson(packageId: "pkg-e2e-direct", resourceSemanticId: "parcels", newFieldName: "owner_email"),
        });

        result.Outcome.Should().Be(OperationGatewayOutcome.Executed);
        result.ExecutionOperationId.Should().NotBeNullOrEmpty();
        var stored = await workflowStore.GetAsync(result.ExecutionOperationId!);
        stored.Should().NotBeNull("a release operation must actually have been created, not just claimed as Executed");
        stored!.MetadataRelease!.PackageId.Should().Be("pkg-e2e-direct");
    }

    [UnitTest]
    public async Task Gateway_RequiresApprovalTier_ProposeThenApprove_CreatesReleaseOperation()
    {
        var workflowStore = new InMemoryWorkflowOperationStore();
        var executor = new MetadataReleaseOperationExecutor(new MetadataReleaseControlService([workflowStore]));
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.MetadataRelease).Returns(
            new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.MetadataRelease, HonuaEdition.Enterprise, "test"));

        var proposalStore = new InMemoryProposalStore();
        var gateway = BuildGateway(proposalStore, executor, ladder);

        var payload = CreatePayloadJson(packageId: "pkg-e2e-approve", resourceSemanticId: "parcels", newFieldName: "owner_email");
        var proposeResult = await gateway.RouteAsync(new OperationGatewayRequest
        {
            Kind = OperationClass.MetadataRelease,
            RequestedBy = "agent:proposer",
            ExecutionPayload = payload,
        });

        proposeResult.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated);
        proposeResult.ProposalId.Should().NotBeNullOrEmpty();

        var approved = await gateway.ApplyApprovedProposalAsync(proposeResult.ProposalId!, "admin");

        approved.Should().NotBeNull();
        approved!.Status.Should().Be(OperationProposalStatus.Submitted);
        approved.ExecutionOperationId.Should().NotBeNullOrEmpty();

        var stored = await workflowStore.GetAsync(approved.ExecutionOperationId!);
        stored.Should().NotBeNull("approving the proposal must create the underlying metadata-release operation");
        stored!.MetadataRelease!.PackageId.Should().Be("pkg-e2e-approve");
    }

    [UnitTest]
    public async Task Gateway_SeedKind_ReturnsNotSupported_NoExecutorRegistered()
    {
        // Production-shaped executor set: MetadataRelease (real), plus stand-ins for
        // AdminConfigChange/Deploy. Seed has none — the gateway must return NotSupported,
        // never claim Executed with no work performed (BH6-032).
        var executors = new IOperationExecutor[]
        {
            new MetadataReleaseOperationExecutor(new MetadataReleaseControlService([new InMemoryWorkflowOperationStore()])),
            new StubExecutor(OperationClass.AdminConfigChange),
            new StubExecutor(OperationClass.Deploy),
        };

        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.Seed).Returns(
            new GuardrailDecision(GuardrailTier.DirectExecute, OperationClass.Seed, HonuaEdition.Community, "test"));

        var gateway = BuildGateway(new InMemoryProposalStore(), executors, ladder);

        var result = await gateway.RouteAsync(new OperationGatewayRequest { Kind = OperationClass.Seed });

        result.Outcome.Should().Be(OperationGatewayOutcome.NotSupported);
        result.ExecutionOperationId.Should().BeNull();
    }

    // ── IOperationExecutorCatalog: truthful supportedKinds ─────────────────────

    [UnitTest]
    public void OperationExecutorCatalog_ReflectsRegisteredExecutors_MetadataReleaseInSeedOut()
    {
        var executors = new IOperationExecutor[]
        {
            new MetadataReleaseOperationExecutor(new MetadataReleaseControlService([new InMemoryWorkflowOperationStore()])),
            new StubExecutor(OperationClass.AdminConfigChange),
            new StubExecutor(OperationClass.Deploy),
        };

        var catalog = new OperationExecutorCatalog(executors);

        catalog.SupportedKinds.Should().BeEquivalentTo(new[]
        {
            OperationClass.AdminConfigChange,
            OperationClass.Deploy,
            OperationClass.MetadataRelease,
        });
        catalog.SupportedKinds.Should().NotContain(OperationClass.Seed,
            "Seed has no runner on trunk (descoped by design, #2563) and must never be advertised as routable");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static string CreatePayloadJson(
        string action = "create",
        string packageId = "pkg-1",
        string targetEnvironment = "production",
        string resourceSemanticId = "parcels",
        string newFieldName = "owner_email",
        string? newFieldType = "String")
        => $$"""
        {
          "action": "{{action}}",
          "packageId": "{{packageId}}",
          "targetEnvironment": "{{targetEnvironment}}",
          "resourceSemanticId": "{{resourceSemanticId}}",
          "newFieldName": "{{newFieldName}}",
          "newFieldType": {{(newFieldType is null ? "null" : $"\"{newFieldType}\"")}}
        }
        """;

    private static OperationGateway BuildGateway(
        IOperationProposalStore proposalStore,
        IOperationExecutor executor,
        IGuardrailLadder ladder)
        => BuildGateway(proposalStore, new[] { executor }, ladder);

    private static OperationGateway BuildGateway(
        IOperationProposalStore proposalStore,
        IEnumerable<IOperationExecutor> executors,
        IGuardrailLadder ladder)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAuditLog>(_ => NullAuditLog.Instance);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var notifier = Substitute.For<IProposalNotifier>();

        return new OperationGateway(
            ladder,
            proposalStore,
            executors,
            scopeFactory,
            notifier,
            NullLogger<OperationGateway>.Instance);
    }

    /// <summary>Executor stand-in for classes not under test here (AdminConfigChange/Deploy).</summary>
    private sealed class StubExecutor(OperationClass operationClass) : IOperationExecutor
    {
        public OperationClass OperationClass => operationClass;

        public Task<OperationProposalPlan?> PlanAsync(OperationGatewayRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposalPlan?>(null);

        public Task<string?> ExecuteAsync(OperationGatewayRequest request, string? executionPayload, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("stub-op-id");
    }

    /// <summary>Thread-safe in-memory workflow-operation store (mirrors the fixture used elsewhere in this folder).</summary>
    private sealed class InMemoryWorkflowOperationStore : IWorkflowOperationStore
    {
        private readonly ConcurrentDictionary<string, WorkflowOperationRecord> _operations = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _metadataPackageIndex = new(StringComparer.Ordinal);
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
        {
            var created = _operations.TryAdd(operation.OperationId, operation);
            if (created)
            {
                Index(operation);
            }

            return Task.FromResult(created);
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
            Index(operation);
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            Index(operation);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
        {
            var operations = _operations.Values
                .Where(operation => !kind.HasValue || operation.Kind == kind.Value)
                .ToArray();
            return Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(operations);
        }

        private void Index(WorkflowOperationRecord operation)
        {
            if (operation.Kind == WorkflowOperationKind.MetadataRelease &&
                !string.IsNullOrWhiteSpace(operation.MetadataRelease?.PackageId))
            {
                _metadataPackageIndex[operation.MetadataRelease.PackageId] = operation.OperationId;
            }
        }
    }

    /// <summary>Thread-safe in-memory proposal store with CAS semantics (mirrors the state-machine tests fixture).</summary>
    private sealed class InMemoryProposalStore : IOperationProposalStore
    {
        private readonly ConcurrentDictionary<string, OperationProposal> _proposals = new(StringComparer.Ordinal);

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

        public Task<bool> TryCreateAsync(OperationProposal proposal, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            if (_proposals.ContainsKey(proposal.ProposalId))
            {
                return Task.FromResult(false);
            }

            _proposals[proposal.ProposalId] = proposal with { Version = 1 };
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

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
