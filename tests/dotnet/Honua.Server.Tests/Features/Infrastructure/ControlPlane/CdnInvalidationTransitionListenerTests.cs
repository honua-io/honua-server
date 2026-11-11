// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class CdnInvalidationTransitionListenerTests
{
    private readonly ICdnInvalidator _invalidator = Substitute.For<ICdnInvalidator>();
    private readonly IWorkflowOperationStore _store = Substitute.For<IWorkflowOperationStore>();

    [UnitTheory]
    [InlineData(WorkflowOperationStatus.Succeeded, WorkflowOperationTransitionKind.Promoted)]
    [InlineData(WorkflowOperationStatus.RolledBack, WorkflowOperationTransitionKind.RolledBack)]
    public async Task OnTransitionAsync_ReleasePromotedOrRolledBack_InvalidatesEverythingOnce(
        WorkflowOperationStatus status, WorkflowOperationTransitionKind kind)
    {
        Returns(new CdnInvalidationResult(CdnInvalidationOutcome.Submitted));
        var listener = Listener();

        await listener.OnTransitionAsync(Transition(status, kind));
        await listener.OnTransitionAsync(Transition(status, kind));

        await _invalidator.Received(1).InvalidateAsync(
            Arg.Is<IReadOnlyCollection<string>>(paths => paths.SequenceEqual(new[] { "/*" })), Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().TrySetAsync(default!, default, default);
    }

    [UnitTest]
    public async Task OnTransitionAsync_PromotedThenRolledBack_InvalidatesForEach()
    {
        Returns(new CdnInvalidationResult(CdnInvalidationOutcome.Submitted));
        var listener = Listener();

        await listener.OnTransitionAsync(Transition(WorkflowOperationStatus.Succeeded, WorkflowOperationTransitionKind.Promoted));
        await listener.OnTransitionAsync(Transition(WorkflowOperationStatus.RolledBack, WorkflowOperationTransitionKind.RolledBack));
        await listener.OnTransitionAsync(Transition(WorkflowOperationStatus.RolledBack, WorkflowOperationTransitionKind.RolledBack));

        await _invalidator.Received(2).InvalidateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task OnTransitionAsync_RollbackOnlyRequested_DoesNotInvalidateYet()
    {
        await Listener().OnTransitionAsync(
            Transition(WorkflowOperationStatus.RollbackRequested, WorkflowOperationTransitionKind.RolledBack));

        await _invalidator.DidNotReceiveWithAnyArgs().InvalidateAsync(default!, default);
    }

    [UnitTest]
    public async Task OnTransitionAsync_InvalidationFailsAfterRollback_IsRecordedOnTheOperationWithoutThrowing()
    {
        Returns(new CdnInvalidationResult(CdnInvalidationOutcome.Failed, "AccessDenied"));
        var rolledBack = Transition(WorkflowOperationStatus.RolledBack, WorkflowOperationTransitionKind.RolledBack);

        await Listener().OnTransitionAsync(rolledBack);

        await _store.Received(1).TrySetAsync(
            Arg.Is<WorkflowOperationRecord>(operation =>
                operation.Status == WorkflowOperationStatus.RolledBack
                && operation.Warnings.Single().StartsWith(CdnInvalidationTransitionListener.WarningPrefix)
                && operation.Warnings.Single().Contains("AccessDenied")),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    private void Returns(CdnInvalidationResult result)
        => _invalidator.InvalidateAsync(default!, default).ReturnsForAnyArgs(result);

    private CdnInvalidationTransitionListener Listener()
        => new(
            new CdnInvalidationService(_invalidator, TimeProvider.System, NullLogger<CdnInvalidationService>.Instance),
            new ServiceCollection().AddSingleton(_store).BuildServiceProvider());

    private static WorkflowOperationTransition Transition(WorkflowOperationStatus status, WorkflowOperationTransitionKind kind)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowOperationTransition
        {
            Operation = new WorkflowOperationRecord
            {
                OperationId = "op-1",
                Kind = WorkflowOperationKind.Deploy,
                Status = status,
                CreatedAt = now,
                UpdatedAt = now,
                CurrentPhase = "Done",
            },
            Kind = kind,
            OccurredAt = now,
        };
    }
}
