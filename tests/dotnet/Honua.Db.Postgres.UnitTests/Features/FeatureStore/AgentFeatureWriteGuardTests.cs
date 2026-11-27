// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Postgres.UnitTests.Features.FeatureStore;

public sealed class AgentFeatureWriteGuardTests
{
    private readonly IFeatureQueryBuilder _queryBuilder = Substitute.For<IFeatureQueryBuilder>();
    private readonly IFeatureDataAccess _dataAccess = Substitute.For<IFeatureDataAccess>();
    private readonly IFeatureCacheManager _cacheManager = Substitute.For<IFeatureCacheManager>();
    private readonly PostgresFeatureStoreRefactored _store;

    public AgentFeatureWriteGuardTests()
    {
        _store = new PostgresFeatureStoreRefactored(_queryBuilder, _dataAccess, _cacheManager);
    }

    [UnitTest]
    public async Task WriteOperations_WhenAgentWriteGuardIsBlocked_ThrowResourceAccessDeniedWithoutTouchingDataAccess()
    {
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty);
        var batch = new FeatureEditBatch { Creates = [feature] };
        var receipt = new FeatureEditIdempotencyReceipt
        {
            Scope = "edit-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Payload = static _ => ReadOnlyMemory<byte>.Empty,
        };

        using var _ = AgentFeatureWriteGuard.Begin(static () => true);

        await ((Func<Task>)(() => _store.CreateAsync(1, feature)))
            .Should().ThrowAsync<AgentFeatureWriteApprovalRequiredException>();
        await ((Func<Task>)(() => _store.UpdateAsync(1, feature)))
            .Should().ThrowAsync<AgentFeatureWriteApprovalRequiredException>();
        await ((Func<Task>)(() => _store.DeleteAsync(1, 1)))
            .Should().ThrowAsync<AgentFeatureWriteApprovalRequiredException>();
        await ((Func<Task>)(() => _store.ApplyEditsAsync(1, batch)))
            .Should().ThrowAsync<AgentFeatureWriteApprovalRequiredException>();
        await ((Func<Task>)(() => _store.ApplyEditsAsync(1, batch, receipt)))
            .Should().ThrowAsync<AgentFeatureWriteApprovalRequiredException>();
        await ((Func<Task>)(() => _store.BeginTransactionAsync()))
            .Should().ThrowAsync<AgentFeatureWriteApprovalRequiredException>();

        _dataAccess.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public async Task WriteOperations_WhenAgentWriteGuardAllows_DelegateToDataAccess()
    {
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty);
        _dataAccess.CreateFeatureAsync(1, feature, Arg.Any<CancellationToken>()).Returns(feature);

        using var _ = AgentFeatureWriteGuard.Begin(static () => false);
        var created = await _store.CreateAsync(1, feature);

        created.Should().Be(feature);
        await _dataAccess.Received(1).CreateFeatureAsync(1, feature, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public void AgentFeatureWriteApprovalRequiredException_DerivesFromResourceAccessDeniedException()
    {
        var exception = new AgentFeatureWriteApprovalRequiredException();

        exception.Should().BeAssignableTo<ResourceAccessDeniedException>();
        exception.Message.Should().Contain("human approval");
    }
}
