// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.FeatureStore;

public sealed partial class BranchVersioningIntegrationTests
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.ApplyEdits)]
    public async Task ApplyEditsAsync_RepeatedObjectUpdates_ReturnsDistinctOperationEvidence(bool atomic)
    {
        var store = CreateFeatureStore();
        var original = await store.CreateAsync(PointsLayerId, BuildFeature("before", 1, 1), CancellationToken.None);
        using var capture = FeatureOperationEvidenceScope.Begin();
        var batch = FeatureEditBatch.Create(operations:
        [
            FeatureEditOperation.Update(BuildFeature("first", 2, 2, original.Id)),
            FeatureEditOperation.Update(BuildFeature("second", 3, 3, original.Id))
        ], rollbackOnFailure: atomic);

        var result = await store.ApplyEditsAsync(PointsLayerId, batch, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.UpdateResults.Should().HaveCount(2);
        result.UpdateResults.Should().OnlyContain(operation => !operation.OperationEvidence.IsDefault);
        var first = result.UpdateResults[0].OperationEvidence.Should().ContainSingle().Which;
        var second = result.UpdateResults[1].OperationEvidence.Should().ContainSingle().Which;
        first.ObjectId.Should().Be(original.Id);
        first.LayerId.Should().Be(PointsLayerId);
        first.Operation.Should().Be(FeatureChangeOperation.Update);
        second.ObjectId.Should().Be(original.Id);
        second.Generation.Should().BeGreaterThan(first.Generation);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    public async Task ApplyEditsAsync_CaptureNotRequested_LeavesOperationEvidenceUnavailable()
    {
        var result = await CreateFeatureStore().ApplyEditsAsync(PointsLayerId,
            FeatureEditBatch.Create(creates: [BuildFeature("ordinary", 1, 1)]), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.CreateResults.Should().ContainSingle().Which.OperationEvidence.IsDefault.Should().BeTrue();
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.ApplyEdits)]
    public async Task ApplyEditsAsync_CreateBatch_CapturesEachOperation(bool atomic)
    {
        using var capture = FeatureOperationEvidenceScope.Begin();
        var result = await CreateFeatureStore().ApplyEditsAsync(PointsLayerId,
            FeatureEditBatch.Create(creates: [BuildFeature("one", 1, 1), BuildFeature("two", 2, 2)],
                rollbackOnFailure: atomic), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.CreateResults.Should().HaveCount(2);
        foreach (var operation in result.CreateResults)
        {
            var evidence = operation.OperationEvidence.Should().ContainSingle().Which;
            evidence.ObjectId.Should().Be(operation.ObjectId);
            evidence.Operation.Should().Be(FeatureChangeOperation.Insert);
        }
        result.CreateResults[0].OperationEvidence[0].Generation.Should()
            .BeLessThan(result.CreateResults[1].OperationEvidence[0].Generation);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.ApplyEdits)]
    public async Task ApplyEditsAsync_FailedAtomicBatch_DiscardsOperationEvidence(bool ordered)
    {
        using var capture = FeatureOperationEvidenceScope.Begin();
        var feature = BuildFeature("rolled-back", 1, 1);
        var batch = ordered
            ? FeatureEditBatch.Create(operations: [FeatureEditOperation.Create(feature),
                FeatureEditOperation.Update(BuildFeature("missing", 2, 2, long.MaxValue))], rollbackOnFailure: true)
            : FeatureEditBatch.Create(creates: [feature], updates: [BuildFeature("missing", 2, 2, long.MaxValue)],
                rollbackOnFailure: true);

        var result = await CreateFeatureStore().ApplyEditsAsync(PointsLayerId, batch, CancellationToken.None);

        result.WasRolledBack.Should().BeTrue();
        result.CreateResults.Should().OnlyContain(operation => operation.OperationEvidence.IsDefault);
        result.UpdateResults.Should().OnlyContain(operation => operation.OperationEvidence.IsDefault);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    public async Task ApplyEditsAsync_VersionedDelete_CapturesLogicalDelete()
    {
        var store = CreateFeatureStore();
        var original = await store.CreateAsync(PointsLayerId, BuildFeature("original", 1, 1), CancellationToken.None);
        var version = await CreateVersionManager().CreateAsync(
            new CreateVersionRequest("evidence-delete", "sde", VersionAccess.Public), CancellationToken.None);
        using var capture = FeatureOperationEvidenceScope.Begin();

        var result = await store.ApplyEditsAsync(PointsLayerId,
            FeatureEditBatch.Create(deletes: [original.Id], versionContext: VersionContext.ForVersion(version)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var evidence = result.DeleteResults.Should().ContainSingle().Which.OperationEvidence.Should().ContainSingle().Which;
        evidence.Operation.Should().Be(FeatureChangeOperation.Delete);
        evidence.VersionId.Should().Be(version.VersionId);
        evidence.ObjectId.Should().Be(original.Id);
    }

}
