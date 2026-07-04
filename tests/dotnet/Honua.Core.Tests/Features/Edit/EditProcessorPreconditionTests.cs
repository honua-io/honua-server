// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
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

    [UnitTest]
    public void ToFeatureEditBatch_EmitsActivitySpan()
    {
        // PA-018: the feature-write pipeline (validate/optimize/convert) previously emitted no
        // telemetry span at all.
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Honua.Core.Edit",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var request = UnifiedEditRequest.WithUpdates(
            ImmutableArray.Create(EditFeature.ForUpdate(5, null, ImmutableDictionary<string, object?>.Empty)));

        CreateProcessor().ToFeatureEditBatch(request, Resource);

        activities.Should().ContainSingle(activity =>
            activity.OperationName == "edit.tofeaturebatch" &&
            activity.TagObjects.Any(tag => tag.Key == "honua.resource.id"));
    }


    [UnitTest]
    public void ValidateCreate_SpatialLayerWithNullGeometry_ReturnsFailure()
    {
        // BH-014: Adding a feature to a spatial layer without geometry must return a
        // clean validation failure rather than hitting a DB NOT NULL constraint.
        var processor = CreateProcessor();

        var resource = new MetadataV2Resource
        {
            Spatial = new Honua.Core.Features.Metadata.Domain.V2.MetadataV2ResourceSpatial
            {
                GeometryType = Honua.Core.Features.Metadata.Domain.V2.MetadataV2GeometryType.Point
            }
        };

        var feature = EditFeature.ForCreate(
            geometry: null,
            ImmutableDictionary<string, object?>.Empty);

        var request = UnifiedEditRequest.WithCreates(ImmutableArray.Create(feature));

        var result = processor.ValidateEdit(request, resource);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Geometry is required");
    }

    [UnitTest]
    public void ValidateCreate_SpatialLayerWithGeometry_Succeeds()
    {
        // BH-014 complement: a create with geometry on a spatial layer must still pass.
        var processor = CreateProcessor();

        var resource = new MetadataV2Resource
        {
            Spatial = new Honua.Core.Features.Metadata.Domain.V2.MetadataV2ResourceSpatial
            {
                GeometryType = Honua.Core.Features.Metadata.Domain.V2.MetadataV2GeometryType.Point
            }
        };

        // Minimal WKB for a point (not validated in this test path)
        var wkb = new byte[] { 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var feature = EditFeature.ForCreate(
            geometry: wkb,
            ImmutableDictionary<string, object?>.Empty);

        var request = UnifiedEditRequest.WithCreates(ImmutableArray.Create(feature));

        var result = processor.ValidateEdit(request, resource);

        result.IsValid.Should().BeTrue();
    }
    [UnitTest]
    public void ValidateEdit_EmptyRequest_EmitsErrorStatusOnSpan()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Honua.Core.Edit",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var result = CreateProcessor().ValidateEdit(new UnifiedEditRequest(), Resource);

        result.IsValid.Should().BeFalse();
        activities.Should().ContainSingle(activity =>
            activity.OperationName == "edit.validate" &&
            activity.Status == ActivityStatusCode.Error);
    }
}
