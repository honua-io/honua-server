// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.FeatureStore.Domain;

/// <summary>
/// Unit coverage for <see cref="FeatureEditBatch"/>, <see cref="FeatureEditOperation"/>,
/// <see cref="FeatureEditResult"/>, and <see cref="EditOperationResult"/>. These
/// govern GeoServices applyEdits rollback semantics and per-row reporting —
/// drift here changes externally visible JSON output (#1144).
/// </summary>
public sealed class FeatureEditBatchTests
{
    private static Feature SampleFeature(long id) => Feature.Create(id, geometry: null);

    [UnitTest]
    public void DefaultConstructor_PopulatesEmptyArrays()
    {
        var batch = new FeatureEditBatch();

        batch.Operations.Should().BeEmpty();
        batch.Creates.Should().BeEmpty();
        batch.Updates.Should().BeEmpty();
        batch.Deletes.Should().BeEmpty();
        batch.RollbackOnFailure.Should().BeFalse();
        batch.UseGlobalIds.Should().BeFalse();
        batch.IsEmpty.Should().BeTrue();
        batch.TotalOperations.Should().Be(0);
    }

    [UnitTest]
    public void Create_DefaultArguments_ProduceEmptyArrays()
    {
        var batch = FeatureEditBatch.Create();

        batch.Creates.Should().BeEmpty();
        batch.Updates.Should().BeEmpty();
        batch.Deletes.Should().BeEmpty();
        batch.IsEmpty.Should().BeTrue();
    }

    [UnitTest]
    public void Create_WithLists_AggregatesTotal()
    {
        var batch = FeatureEditBatch.Create(
            creates: ImmutableArray.Create(SampleFeature(1), SampleFeature(2)),
            updates: ImmutableArray.Create(SampleFeature(3)),
            deletes: ImmutableArray.Create(4L, 5L, 6L));

        batch.TotalOperations.Should().Be(6);
        batch.IsEmpty.Should().BeFalse();
    }

    [UnitTest]
    public void TotalOperations_PrefersOrderedOperationsList()
    {
        // When Operations is populated, the ordered list takes precedence so
        // we don't double-count features that already appear in the operations.
        var batch = FeatureEditBatch.Create(
            creates: ImmutableArray.Create(SampleFeature(10)),
            operations: ImmutableArray.Create(
                FeatureEditOperation.Create(SampleFeature(10)),
                FeatureEditOperation.Delete(20)));

        batch.TotalOperations.Should().Be(2);
    }

    [UnitTest]
    public void Create_PreservesFlags()
    {
        var batch = FeatureEditBatch.Create(
            creates: ImmutableArray.Create(SampleFeature(1)),
            rollbackOnFailure: true,
            useGlobalIds: true);

        batch.RollbackOnFailure.Should().BeTrue();
        batch.UseGlobalIds.Should().BeTrue();
    }

    [UnitTest]
    public void OperationFactories_SetExpectedKind()
    {
        var create = FeatureEditOperation.Create(SampleFeature(1));
        var update = FeatureEditOperation.Update(SampleFeature(2));
        var delete = FeatureEditOperation.Delete(3);

        create.Kind.Should().Be(FeatureEditOperationKind.Create);
        create.Feature.Should().NotBeNull();
        create.ObjectId.Should().BeNull();

        update.Kind.Should().Be(FeatureEditOperationKind.Update);
        update.Feature.Should().NotBeNull();

        delete.Kind.Should().Be(FeatureEditOperationKind.Delete);
        delete.ObjectId.Should().Be(3);
        delete.Feature.Should().BeNull();
    }

    [UnitTest]
    public void Success_FactoryUsesEmptyArrays_WhenArgumentsOmitted()
    {
        var result = FeatureEditResult.Success(1, 2, 3);

        result.CreatedCount.Should().Be(1);
        result.UpdatedCount.Should().Be(2);
        result.DeletedCount.Should().Be(3);
        result.CreatedIds.Should().BeEmpty();
        result.CreateResults.Should().BeEmpty();
        result.UpdateResults.Should().BeEmpty();
        result.DeleteResults.Should().BeEmpty();
        result.WasRolledBack.Should().BeFalse();
        result.IsSuccess.Should().BeTrue();
        result.HasErrors.Should().BeFalse();
    }

    [UnitTest]
    public void Success_WithFailingChildResult_ReportsHasErrors()
    {
        var result = FeatureEditResult.Success(
            createdCount: 1,
            updatedCount: 0,
            deletedCount: 0,
            createResults: ImmutableArray.Create(EditOperationResult.Failure("nope", objectId: 7)));

        result.HasErrors.Should().BeTrue();
        result.IsSuccess.Should().BeFalse();
    }

    [UnitTest]
    public void Rollback_MarksAllZeroAndFlipsSuccessfulResultsToFailure()
    {
        // Rollback() must convert previously successful per-row results into
        // failure rows so clients see a consistent rolled-back outcome.
        var creates = ImmutableArray.Create(EditOperationResult.Success(1));
        var updates = ImmutableArray.Create(
            EditOperationResult.Success(2),
            EditOperationResult.Failure("already-bad", objectId: 3));
        var deletes = ImmutableArray.Create(EditOperationResult.Success(4));

        var result = FeatureEditResult.Rollback(creates, updates, deletes);

        result.WasRolledBack.Should().BeTrue();
        result.CreatedCount.Should().Be(0);
        result.UpdatedCount.Should().Be(0);
        result.DeletedCount.Should().Be(0);
        result.CreatedIds.Should().BeEmpty();
        result.IsSuccess.Should().BeFalse();
        result.HasErrors.Should().BeTrue();

        // Successful creates lose their ObjectId on rollback (createResults pass includeObjectId: false).
        result.CreateResults.Should().ContainSingle();
        result.CreateResults[0].IsSuccess.Should().BeFalse();
        result.CreateResults[0].ObjectId.Should().BeNull();
        result.CreateResults[0].ErrorMessage.Should().Be("Operation rolled back.");

        // Update results keep their ObjectId on the synthesized rollback failure.
        result.UpdateResults.Should().HaveCount(2);
        result.UpdateResults[0].IsSuccess.Should().BeFalse();
        result.UpdateResults[0].ObjectId.Should().Be(2);
        result.UpdateResults[0].ErrorMessage.Should().Be("Operation rolled back.");

        // Already-failed rows are preserved as-is.
        result.UpdateResults[1].IsSuccess.Should().BeFalse();
        result.UpdateResults[1].ErrorMessage.Should().Be("already-bad");
        result.UpdateResults[1].ObjectId.Should().Be(3);

        // Delete row should be carried over with its ObjectId intact.
        result.DeleteResults[0].IsSuccess.Should().BeFalse();
        result.DeleteResults[0].ObjectId.Should().Be(4);
    }

    [UnitTest]
    public void Rollback_WithDefaultArguments_ReturnsEmptyArrays()
    {
        var result = FeatureEditResult.Rollback();

        result.CreateResults.Should().BeEmpty();
        result.UpdateResults.Should().BeEmpty();
        result.DeleteResults.Should().BeEmpty();
        result.WasRolledBack.Should().BeTrue();
    }

    [UnitTest]
    public void EditOperationResult_DefaultIsSuccessTrue()
    {
        var result = new EditOperationResult();

        result.IsSuccess.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.ErrorCode.Should().Be(0);
    }

    [UnitTest]
    public void EditOperationResult_SuccessFactory_SetsObjectIdAndGlobalId()
    {
        var result = EditOperationResult.Success(objectId: 42, globalId: "abc");

        result.IsSuccess.Should().BeTrue();
        result.ObjectId.Should().Be(42);
        result.GlobalId.Should().Be("abc");
        result.ErrorMessage.Should().BeNull();
    }

    [UnitTest]
    public void EditOperationResult_FailureFactory_UsesDefaultErrorCode()
    {
        var result = EditOperationResult.Failure("bad");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("bad");
        result.ErrorCode.Should().Be(1000);
        result.ObjectId.Should().BeNull();
    }

    [UnitTest]
    public void EditOperationResult_FailureFactory_AcceptsCustomErrorCodeAndObjectId()
    {
        var result = EditOperationResult.Failure("bad", errorCode: 1234, objectId: 7);

        result.ErrorCode.Should().Be(1234);
        result.ObjectId.Should().Be(7);
    }

    [UnitTest]
    public void FeatureEditOperationKind_HasExpectedMembers()
    {
        Enum.GetValues<FeatureEditOperationKind>().Should().Contain(new[]
        {
            FeatureEditOperationKind.Create,
            FeatureEditOperationKind.Update,
            FeatureEditOperationKind.Delete,
        });
    }
}
