// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Edit;

public sealed class EditorTrackingBatchTests
{
    public enum BatchShape { Split, Ordered }

    [UnitTheory]
    [InlineData(BatchShape.Split)]
    [InlineData(BatchShape.Ordered)]
    public void Tracking_IsServerOwned_InBothBatchShapes(BatchShape shape)
    {
        var resource = new MetadataV2Object
        {
            Metadata = new() { Id = "items", Name = "items" },
            Type = MetadataV2ObjectType.FeatureDataset,
            Editing = new() { CreatorField = "creator", CreatedAtField = "created", EditorField = "editor", UpdatedAtField = "updated" },
        };
        var feature = Feature.Create(7, null, ImmutableDictionary<string, object?>.Empty
            .Add("CREATOR", "forged").Add("created", "forged").Add("editor", "forged").Add("updated", "forged").Add("name", "item")) with
        {
            ExplicitAttributeRemovals = ["CREATOR", "updated", "other"],
        };
        var batch = FeatureEditBatch.Create(creates: [feature], updates: [feature], deletes: [9], operations: shape == BatchShape.Ordered
            ? [FeatureEditOperation.Create(feature), FeatureEditOperation.Update(feature), FeatureEditOperation.Delete(9)] : default);

        var omitted = EditorTrackingBatch.OmitClientValues(resource, batch);
        omitted.Updates[0].Attributes.Keys.Should().Equal("name");
        omitted.Updates[0].PreservedAttributeNames.Should().BeEquivalentTo(["creator", "created"]);
        omitted.Updates[0].ExplicitAttributeRemovals.Should().Equal("other");
        // Even a calculation cannot forge a server-owned stamp.
        omitted = omitted with { Updates = [omitted.Updates[0] with { Attributes = omitted.Updates[0].Attributes.Add("editor", "rule") }] };
        var stamped = EditorTrackingBatch.Stamp(resource, omitted, new("alice", static _ => 123L));

        stamped.Creates[0].Attributes.Should().Contain("creator", "alice").And.Contain("created", 123L);
        stamped.Updates[0].Attributes.Should().Contain("editor", "alice").And.Contain("updated", 123L).And.NotContainKey("created").And.NotContainKey("creator");
        stamped.Deletes.Should().Equal(9);
        if (shape == BatchShape.Ordered)
        {
            stamped.Operations[0].Feature!.Value.Attributes.Should().Contain("creator", "alice");
            stamped.Operations[1].Feature!.Value.Attributes.Should().Contain("editor", "alice").And.NotContainKey("creator");
            stamped.Operations[2].ObjectId.Should().Be(9);
        }
    }
}
