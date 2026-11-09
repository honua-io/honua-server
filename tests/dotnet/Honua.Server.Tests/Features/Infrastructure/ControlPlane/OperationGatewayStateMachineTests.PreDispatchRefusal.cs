// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using IOperationInstanceStore = Honua.Core.Features.Operations.Abstractions.IOperationInstanceStore;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class OperationGatewayStateMachineTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ApplyApprovedProposal_PostAuditProposerRefusal_TerminatesBothRecordsWithoutActuating()
    {
        var scenario = new PostAuditRefusalScenario();
        Assert.True(await scenario.Instances.TryCreateAsync(scenario.Accepted));

        var resolved = await scenario.Gateway.ApplyApprovedProposalAsync(scenario.Proposal.ProposalId, "human-approver");

        var persisted = await scenario.Proposals.GetAsync(scenario.Proposal.ProposalId);
        Assert.Equal(OperationProposalStatus.Failed, resolved!.Status);
        Assert.Equal(OperationProposalStatus.Failed, persisted!.Status);
        Assert.False(scenario.Executor.Entered.Task.IsCompleted);
        Assert.Equal(2, scenario.AuthorityChecks);
        Assert.Contains(scenario.Audit.Events, entry => entry.Action == "operation.approved");
        var handle = await scenario.Instances.GetAsync(scenario.Accepted.OperationInstanceId);
        Assert.Equal(OperationHandleStatus.Failed, handle!.Status);
        Assert.Equal(persisted.ResolutionReason, handle.Reason);
        Assert.Equal(scenario.Proposal.ProposalId, handle.ProposalId);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ApplyApprovedProposal_PostAuditCanonicalWriteFails_ProposalRemainsFailedWithoutActuating()
    {
        var instances = Substitute.For<IOperationInstanceStore>();
        var scenario = new PostAuditRefusalScenario(instances);
        instances.GetAsync(scenario.Accepted.OperationInstanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OperationHandle?>(scenario.Accepted));
        instances.SetAsync(Arg.Any<OperationHandle>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("Canonical store unavailable")));

        var error = await Record.ExceptionAsync(() =>
            scenario.Gateway.ApplyApprovedProposalAsync(scenario.Proposal.ProposalId, "human-approver"));

        var persisted = await scenario.Proposals.GetAsync(scenario.Proposal.ProposalId);
        Assert.Equal(OperationProposalStatus.Failed, persisted!.Status);
        Assert.False(scenario.Executor.Entered.Task.IsCompleted);
        Assert.Equal(2, scenario.AuthorityChecks);
        Assert.IsType<IOException>(error);
        await instances.Received(1).SetAsync(Arg.Is<OperationHandle>(handle =>
            handle.Status == OperationHandleStatus.Failed && handle.Reason == persisted.ResolutionReason),
            Arg.Any<CancellationToken>());
    }

    private sealed class PostAuditRefusalScenario
    {
        private readonly Guid _keyId = Guid.NewGuid();
        public OperationProposal Proposal { get; }
        public InMemoryProposalStore Proposals { get; }
        public IOperationInstanceStore Instances { get; }
        public OperationHandle Accepted { get; }
        public PendingApprovalExecutor Executor { get; } = new();
        public RecordingAuditLog Audit { get; } = new();
        public OperationGateway Gateway { get; }
        public int AuthorityChecks { get; private set; }

        public PostAuditRefusalScenario(IOperationInstanceStore? instances = null)
        {
            Proposal = CreateProposal("p-post-audit-refusal", OperationClass.Deploy, OperationProposalStatus.AwaitingApproval)
                with { RequestedBy = $"apikey:api-key:{_keyId:D}" };
            Proposals = new InMemoryProposalStore(Proposal);
            Instances = instances ?? new VolatileOperationInstanceStore();
            Accepted = CreateApprovalHandle(Proposal);
            Executor.Completion.SetResult("execution-id");
            var keys = Substitute.For<IAdminApiKeyStore>();
            keys.GetAsync(_keyId, Arg.Any<CancellationToken>()).Returns(_ =>
                Task.FromResult(++AuthorityChecks == 1 ? CurrentKey() : null));
            Gateway = CanonicalOperationGatewayTestComposition.Build(Proposals, Substitute.For<IGuardrailLadder>(), [Executor],
                configure: services =>
                {
                    services.AddSingleton<IAuditLog>(Audit);
                    services.AddSingleton(Instances);
                    services.AddSingleton(new OperationProposerApiKeyAuthority(TimeProvider.System, keys));
                });
        }

        private AdminApiKeyRecord CurrentKey() => new(_keyId, "proposer", "test-prefix", [], ["admin:*"],
            Proposal.CreatedAt, Proposal.CreatedAt, null, null, null, null, "test");
    }
}
