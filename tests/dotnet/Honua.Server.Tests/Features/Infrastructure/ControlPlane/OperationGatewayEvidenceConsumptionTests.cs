// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class OperationGatewayEvidenceConsumptionTests
{
    [UnitTest]
    public async Task RouteAsync_SignedEvidenceAlreadyUsed_RefusesProposal()
    {
        var store = EvidenceAlreadyUsedStore();
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.Deploy).Returns(
            new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, HonuaEdition.Pro, "test"));
        var gateway = CanonicalOperationGatewayTestComposition.Build(
            store,
            ladder,
            [CanonicalOperationGatewayTestComposition.PlanningOnly(OperationClass.Deploy)]);

        var result = await gateway.RouteAsync(new OperationGatewayRequest
        {
            Kind = OperationClass.Deploy,
            RequestedBy = "agent",
            IdempotencyKey = "evidence-reuse",
        });

        result.Outcome.Should().Be(OperationGatewayOutcome.Blocked);
        result.Message.Should().Be(OperationProposalEvidenceConsumedException.AlreadyUsedMessage);
        result.ProposalId.Should().BeNull();
        await store.Received(1).TryCreateAsync(
            Arg.Any<OperationProposal>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().TrySetAsync(
            Arg.Any<OperationProposal>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    private static IOperationProposalStore EvidenceAlreadyUsedStore()
    {
        var store = Substitute.For<IOperationProposalStore>();
        store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OperationProposal?>(null));
        store.ListActiveAsync(Arg.Any<OperationClass?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OperationProposal>>([]));
        store.TryCreateAsync(Arg.Any<OperationProposal>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new OperationProposalEvidenceConsumedException(
                OperationProposalEvidenceConsumedException.AlreadyUsedMessage));
        return store;
    }
}
