// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresRowSecurityWriteCheckIntegrationTests
{
    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ApplyEdits_ExpectedAbsentDeleteUnderRls_SucceedsWithoutOutboxMutation(bool ordered, bool withOutbox)
    {
        var outbox = new RecordingOutboxWriter();
        _store = CreateStore(outbox);
        using var scope = withOutbox ? BeginOutboxScope() : null;

        var result = await _store.ApplyEditsAsync(LayerId, ExpectedAbsentDelete(ordered, 999));

        result.DeleteResults.Should().ContainSingle().Which.IsSuccess.Should().BeTrue();
        result.DeletedCount.Should().Be(1);
        outbox.Writes.Should().Be(0);
        (await CountRowsAsync("north")).Should().Be(1);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyEdits_ExpectedAbsentDeleteUnderRls_HiddenExistingRowStaysDenied(bool ordered)
    {
        await InsertHiddenRowAsync();

        var result = await _store.ApplyEditsAsync(LayerId, ExpectedAbsentDelete(ordered, 2));

        var operation = result.DeleteResults.Should().ContainSingle().Which;
        operation.IsSuccess.Should().BeFalse();
        operation.ErrorCode.Should().Be(EditOperationResult.NotPermittedErrorCode);
        operation.IsPreconditionFailure.Should().BeFalse();
        operation.PreconditionFailureFeature.Should().BeNull();
        (await ReadAttributesAsync(2)).Should().Be("south|private");
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyEdits_ExpectedAbsentDeleteUnderRls_VisibleExistingRowReturnsPreconditionFailure(bool ordered)
    {
        var result = await _store.ApplyEditsAsync(LayerId, ExpectedAbsentDelete(ordered, NorthRowId));

        var operation = result.DeleteResults.Should().ContainSingle().Which;
        operation.IsSuccess.Should().BeFalse();
        operation.IsPreconditionFailure.Should().BeTrue();
        operation.PreconditionFailureFeature.GetValueOrDefault().Id.Should().Be(NorthRowId);
        (await CountRowsAsync("north")).Should().Be(1);
    }

    private static FeatureEditBatch ExpectedAbsentDelete(bool ordered, long objectId)
    {
        var precondition = new FeatureEditPrecondition { ObjectId = objectId, ExpectedRowAbsent = true };
        return ordered
            ? FeatureEditBatch.Create(operations: [FeatureEditOperation.Delete(objectId)], preconditions: [precondition])
            : FeatureEditBatch.Create(deletes: [objectId], preconditions: [precondition]);
    }
}
