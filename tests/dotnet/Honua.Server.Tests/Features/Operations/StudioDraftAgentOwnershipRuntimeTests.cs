// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Operations;

// The runtime reports whether every owner a Studio call touches is the actor bound to it.
public sealed class StudioDraftAgentOwnershipRuntimeTests
{
    private const string Agent = "agent-key-1";
    private const string Other = "someone-else";
    private static readonly Guid DraftId = Guid.NewGuid();
    private static readonly Guid ItemId = Guid.NewGuid();

    [UnitTheory]
    [InlineData("own draft and item")]
    [InlineData("own draft, keeps owner")]
    public async Task UpdateAsync_OwnDraftAndItem_IsWithinTheActor(string scenario)
        => (await UpdateWithinActorAsync(scenario)).Should().BeTrue();

    [UnitTheory]
    [InlineData("transfers owner")]
    [InlineData("item of another owner")]
    [InlineData("draft of another owner")]
    public async Task UpdateAsync_AnotherOwnerInvolved_IsNotWithinTheActor(string scenario)
        => (await UpdateWithinActorAsync(scenario)).Should().BeFalse();

    [UnitTest]
    public async Task SaveVersionAsync_OwnDraftAndItem_IsWithinTheActor()
        => (await SaveWithinActorAsync(Agent, Agent)).Should().BeTrue();

    [UnitTheory]
    [InlineData(Agent, Other)]
    [InlineData(Other, Agent)]
    public async Task SaveVersionAsync_AnotherOwnersDraftOrItem_IsNotWithinTheActor(string draftOwner, string itemOwner)
        => (await SaveWithinActorAsync(draftOwner, itemOwner)).Should().BeFalse();

    [UnitTheory]
    [InlineData("new item for the caller")]
    [InlineData("caller's existing item")]
    public async Task CreateAsync_CallerOwnsDraftAndItem_IsWithinTheActor(string scenario)
        => (await CreateWithinActorAsync(scenario)).Should().BeTrue();

    [UnitTheory]
    [InlineData("owner set to another")]
    [InlineData("another owner's item")]
    [InlineData("unowned existing item")]
    public async Task CreateAsync_AnotherOwnerInvolved_IsNotWithinTheActor(string scenario)
        => (await CreateWithinActorAsync(scenario)).Should().BeFalse();

    [UnitTest]
    public async Task UpdateAsync_WithoutABoundActor_IsNotWithinTheActor()
    {
        var (runtime, captured) = Build(Agent, Agent);

        await runtime.UpdateAsync(DraftId, UpdateCommand(null) with { ActorId = null }, Context());

        captured().VerifiedStudioDraftWithinActor.Should().BeFalse();
    }

    private static async Task<bool> UpdateWithinActorAsync(string scenario)
    {
        (string Draft, string? Requested, string Item) owners = scenario switch
        {
            "own draft, keeps owner" => (Agent, Agent, Agent),
            "transfers owner" => (Agent, Other, Agent),
            "item of another owner" => (Agent, null, Other),
            "draft of another owner" => (Other, null, Other),
            _ => (Agent, null, Agent),
        };
        var (runtime, captured) = Build(owners.Draft, owners.Item);
        await runtime.UpdateAsync(DraftId, UpdateCommand(owners.Requested), Context());
        return captured().VerifiedStudioDraftWithinActor;
    }

    private static async Task<bool> SaveWithinActorAsync(string draftOwner, string itemOwner)
    {
        var (runtime, captured) = Build(draftOwner, itemOwner);
        await runtime.SaveVersionAsync(DraftId, 1, "save", Agent, Context());
        return captured().VerifiedStudioDraftWithinActor;
    }

    private static async Task<bool> CreateWithinActorAsync(string scenario)
    {
        var (runtime, captured) = Build(Agent, Agent);
        var command = new CreateStudioPackageDraftCommand { PackageKey = "agent-map", Envelope = Envelope(), ActorId = Agent };
        command = scenario switch
        {
            "caller's existing item" => command with { OwnerId = Agent, ExpectedExistingItemPresent = true, ExpectedExistingItemOwnerId = Agent },
            "owner set to another" => command with { OwnerId = Other },
            "another owner's item" => command with { OwnerId = Agent, ExpectedExistingItemPresent = true, ExpectedExistingItemOwnerId = Other },
            "unowned existing item" => command with { ExpectedExistingItemPresent = true },
            _ => command,
        };
        await runtime.CreateAsync(command, Context());
        return captured().VerifiedStudioDraftWithinActor;
    }

    private static (StudioDraftMutationRuntime Runtime, Func<OperationRequest> Captured) Build(string draftOwner, string itemOwner)
    {
        var store = Substitute.For<IStudioPackageStore>();
        store.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns(new StudioPackageDraft
        {
            DraftId = DraftId,
            ItemId = ItemId,
            PackageKey = "agent-map",
            Family = StudioPackageFamily.Map,
            Envelope = Envelope(),
            OwnerId = draftOwner,
            Generation = 1,
        });
        store.GetPointersAsync(ItemId, Arg.Any<CancellationToken>()).Returns(new StudioContentItemPointers { ItemId = ItemId, OwnerId = itemOwner });
        OperationRequest? captured = null;
        var handle = Handle();
        var invoker = Substitute.For<IOperationInvoker>();
        invoker.SubmitAsync(Arg.Do<OperationRequest>(request => captured = request), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
            .Returns(handle);
        var instances = Substitute.For<IOperationInstanceStore>();
        instances.GetAsync(handle.OperationInstanceId, Arg.Any<CancellationToken>()).Returns(handle);
        return (new StudioDraftMutationRuntime(invoker, instances, store), () => captured!);
    }

    private static UpdateStudioPackageDraftCommand UpdateCommand(string? requestedOwner) => new()
    {
        PackageKey = "agent-map",
        OwnerId = requestedOwner,
        Envelope = Envelope(),
        Generation = 1,
        ActorId = Agent,
    };

    private static StudioPackageEnvelope Envelope() => new() { Family = StudioPackageFamily.Map, SchemaVersion = "1.0" };

    private static StudioDraftMutationContext Context() => new() { PrincipalId = "apikey:api-key:agent", CorrelationId = "corr" };

    private static OperationHandle Handle() => new()
    {
        OperationInstanceId = "op-1",
        OperationId = StudioDraftOperations.Update,
        Status = OperationHandleStatus.RequiresApproval,
        CorrelationId = "corr",
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };
}
