// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Core.Features.Studio.Services.Bridging;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed partial class StudioDraftOperationRuntimeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OwnedDraftMutation_OwnershipChangesAfterSubmission_Conflicts(bool delete, bool transferBeforeActuation)
    {
        var store = new InMemoryStudioPackageStore();
        var lifecycle = BuildLifecycle(store);
        var draft = await lifecycle.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "owned-draft-race",
            OwnerId = "studio-author",
            Envelope = PublicationEnvelope("1=1"),
        });
        var instances = new VolatileOperationInstanceStore();
        var invoker = Substitute.For<IOperationInvoker>();
        invoker.SubmitAsync(Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var request = call.Arg<OperationRequest>();
                request.VerifiedStudioDraftOwnerId.Should().Be("studio-author");
                if (transferBeforeActuation)
                {
                    await store.UpdateDraftAsync(draft with { OwnerId = "new-owner" });
                }

                IOperationExecutor executor = delete
                    ? new StudioDraftDeleteExecutor(lifecycle, TimeProvider.System)
                    : new StudioDraftUpdateExecutor(lifecycle, TimeProvider.System);
                var handle = await executor.SubmitAsync(request, Context("owner-race"));
                (await instances.TryCreateAsync(handle)).Should().BeTrue();
                return handle;
            });
        var runtime = new StudioDraftMutationRuntime(invoker, instances, store);
        var context = new StudioDraftMutationContext { PrincipalId = "studio-author" };

        var handle = delete
            ? (await runtime.DeleteAsync(draft.DraftId, context)).Operation
            : (await runtime.UpdateAsync(draft.DraftId, new UpdateStudioPackageDraftCommand
            {
                PackageKey = draft.PackageKey,
                Envelope = PublicationEnvelope("changed"),
            }, context)).Operation;

        if (transferBeforeActuation)
        {
            handle.Status.Should().Be(OperationHandleStatus.Failed);
            handle.Result!.Details["errorKind"].Should().Be("conflict");
            var preserved = await store.GetDraftAsync(draft.DraftId);
            preserved.Should().NotBeNull();
            preserved!.OwnerId.Should().Be("new-owner");
            preserved.Envelope.Should().BeEquivalentTo(draft.Envelope);
        }
        else
        {
            handle.Status.Should().Be(OperationHandleStatus.Completed);
            (await store.GetDraftAsync(draft.DraftId) is null).Should().Be(delete);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task UpdateDraft_GenerationDiffersFromOwnerSnapshot_Conflicts(long generationOffset)
    {
        var store = new InMemoryStudioPackageStore();
        var lifecycle = BuildLifecycle(store);
        var draft = await lifecycle.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "owner-generation",
            OwnerId = "studio-author",
            Envelope = PublicationEnvelope("1=1"),
        });
        var instances = new VolatileOperationInstanceStore();
        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new ServerOperationDescriptorProvider()], TimeProvider.System),
            [new StudioDraftUpdateExecutor(lifecycle, TimeProvider.System)],
            new AllowAllPolicyDecisionPoint(), TimeProvider.System,
            instanceStore: instances, auditLog: new VolatileOperationAuditLog());
        var runtime = new StudioDraftMutationRuntime(dispatcher, instances, store);

        var result = await runtime.UpdateAsync(draft.DraftId, new UpdateStudioPackageDraftCommand
        {
            PackageKey = draft.PackageKey,
            Envelope = PublicationEnvelope("changed"),
            Generation = draft.Generation + generationOffset,
        }, new StudioDraftMutationContext { PrincipalId = "studio-author" });

        result.Operation.Status.Should().Be(OperationHandleStatus.Failed);
        result.Operation.Result!.Details["errorKind"].Should().Be("conflict");
        (await store.GetDraftAsync(draft.DraftId)).Should().BeEquivalentTo(draft);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateAsync_IdenticalCompletedRetry_ReturnsOriginalWithoutAdvancingGeneration(bool explicitGeneration)
    {
        var store = new InMemoryStudioPackageStore();
        var lifecycle = BuildLifecycle(store);
        var draft = await lifecycle.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "idempotent-update",
            OwnerId = "studio-author",
            Envelope = PublicationEnvelope("1=1"),
        });
        var instances = new VolatileOperationInstanceStore();
        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new ServerOperationDescriptorProvider()], TimeProvider.System),
            [new StudioDraftUpdateExecutor(lifecycle, TimeProvider.System)],
            new AllowAllPolicyDecisionPoint(), TimeProvider.System,
            instanceStore: instances, auditLog: new VolatileOperationAuditLog());
        var runtime = new StudioDraftMutationRuntime(dispatcher, instances, store);
        var command = new UpdateStudioPackageDraftCommand
        {
            PackageKey = draft.PackageKey,
            Envelope = PublicationEnvelope("changed"),
            Generation = explicitGeneration ? draft.Generation : null,
        };
        var context = new StudioDraftMutationContext { PrincipalId = "studio-author", IdempotencyKey = "update-once" };

        var first = await runtime.UpdateAsync(draft.DraftId, command, context);
        var retry = await runtime.UpdateAsync(draft.DraftId, command, context);

        first.Operation.Status.Should().Be(OperationHandleStatus.Completed);
        retry.Operation.Status.Should().Be(OperationHandleStatus.Completed);
        retry.Operation.OperationInstanceId.Should().Be(first.Operation.OperationInstanceId);
        retry.Value.Should().BeEquivalentTo(first.Value, options => options
            .Using<JsonElement>(context => JsonElement.DeepEquals(context.Subject, context.Expectation).Should().BeTrue())
            .WhenTypeIs<JsonElement>());
        (await store.GetDraftAsync(draft.DraftId))!.Generation.Should().Be(draft.Generation + 1);
    }

}
