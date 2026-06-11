// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Edit;

/// <summary>
/// Unit coverage for <see cref="EditProcessor.ToFeatureEditBatch"/> precondition mapping:
/// optimistic-concurrency preconditions attached to a <see cref="UnifiedEditRequest"/>
/// (request-level or via <see cref="EditConstraints.ExpectedStateToken"/> on updates) must
/// flow into <see cref="FeatureEditBatch.Preconditions"/> so feature writers can enforce
/// them inside the write transaction.
/// </summary>
public sealed class EditProcessorPreconditionTests
{
    private static readonly MetadataV2Resource Resource = new();

    private static EditProcessor CreateProcessor() => new(NullLogger<EditProcessor>.Instance);

    [UnitTest]
    public void ToFeatureEditBatch_WithoutPreconditions_ProducesEmptyPreconditions()
    {
        var request = UnifiedEditRequest.WithUpdates(
            ImmutableArray.Create(EditFeature.ForUpdate(5, null, ImmutableDictionary<string, object?>.Empty)));

        var batch = CreateProcessor().ToFeatureEditBatch(request, Resource);

        batch.Preconditions.Should().BeEmpty();
    }

    [UnitTest]
    public void ToFeatureEditBatch_RequestLevelPreconditions_FlowIntoBatch()
    {
        var request = UnifiedEditRequest.WithDeletes(ImmutableArray.Create(9L)) with
        {
            Preconditions = ImmutableArray.Create(new FeatureEditPrecondition
            {
                ObjectId = 9,
                ExpectedStateToken = "token-9"
            })
        };

        var batch = CreateProcessor().ToFeatureEditBatch(request, Resource);

        batch.Preconditions.Should().ContainSingle()
            .Which.Should().Be(new FeatureEditPrecondition { ObjectId = 9, ExpectedStateToken = "token-9" });
    }

    [UnitTest]
    public void ToFeatureEditBatch_UpdateConstraintToken_FlowsIntoBatch()
    {
        var update = EditFeature.ForUpdate(
            5,
            null,
            ImmutableDictionary<string, object?>.Empty,
            constraints: EditConstraints.WithExpectedState("token-5", etag: "\"abc\""));
        var request = UnifiedEditRequest.WithUpdates(ImmutableArray.Create(update));

        var batch = CreateProcessor().ToFeatureEditBatch(request, Resource);

        batch.Preconditions.Should().ContainSingle()
            .Which.Should().Be(new FeatureEditPrecondition { ObjectId = 5, ExpectedStateToken = "token-5" });
    }

    [UnitTest]
    public void ToFeatureEditBatch_OrderedUpdateConstraintToken_FlowsIntoBatch()
    {
        var update = EditFeature.ForUpdate(
            11,
            null,
            ImmutableDictionary<string, object?>.Empty,
            constraints: EditConstraints.WithExpectedState("token-11"));
        var request = UnifiedEditRequest.WithOperations(
            ImmutableArray.Create(UnifiedEditOperation.Update(update)));

        var batch = CreateProcessor().ToFeatureEditBatch(request, Resource);

        batch.Operations.Should().HaveCount(1);
        batch.Preconditions.Should().ContainSingle()
            .Which.Should().Be(new FeatureEditPrecondition { ObjectId = 11, ExpectedStateToken = "token-11" });
    }

    [UnitTest]
    public void ToFeatureEditBatch_RequestLevelPrecondition_WinsOverConstraintForSameObjectId()
    {
        var update = EditFeature.ForUpdate(
            5,
            null,
            ImmutableDictionary<string, object?>.Empty,
            constraints: EditConstraints.WithExpectedState("constraint-token"));
        var request = UnifiedEditRequest.WithUpdates(ImmutableArray.Create(update)) with
        {
            Preconditions = ImmutableArray.Create(new FeatureEditPrecondition
            {
                ObjectId = 5,
                ExpectedStateToken = "request-token"
            })
        };

        var batch = CreateProcessor().ToFeatureEditBatch(request, Resource);

        batch.Preconditions.Should().ContainSingle()
            .Which.ExpectedStateToken.Should().Be("request-token");
    }

    [UnitTest]
    public void ToFeatureEditBatch_EtagOnlyConstraint_DoesNotProducePrecondition()
    {
        // A bare protocol ETag is not a canonical state token; only ExpectedStateToken
        // participates in transactional enforcement.
        var update = EditFeature.ForConditionalUpdate(
            5,
            null,
            ImmutableDictionary<string, object?>.Empty,
            etag: "\"abc\"");
        var request = UnifiedEditRequest.WithUpdates(ImmutableArray.Create(update));

        var batch = CreateProcessor().ToFeatureEditBatch(request, Resource);

        batch.Preconditions.Should().BeEmpty();
    }
}
