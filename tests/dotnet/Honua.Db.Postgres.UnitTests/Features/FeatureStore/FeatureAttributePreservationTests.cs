// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureAttributePreservationTests
{
    [UnitTheory]
    [InlineData("objectid")]
    [InlineData("OBJECTID")]
    [InlineData("ObjectId")]
    public void PartialUpdate_DoesNotPersistProjectedObjectId_AndPreservesStoredFields(string idField)
    {
        // Provider reads project the physical ID into the attribute view even
        // when the stored JSON document does not contain that property.
        var current = Feature.Create(42, null,
            ImmutableDictionary<string, object?>.Empty
                .Add(idField, 42L)
                .Add("name", "before")
                .Add("hidden", "keep")
                .Add("nullable", "old value")
                .Add("removed", "delete"));
        var update = Feature.Create(42, null,
            ImmutableDictionary<string, object?>.Empty
                .Add("name", "after")
                .Add("nullable", null)) with
        {
            PreserveOmittedMaskedAttributes = true,
            ExplicitAttributeRemovals = ["removed"]
        };

        var preserved = FeatureDataAccess.PreserveOmittedAttributes(update, current);

        Assert.Equal(42, preserved.Id);
        Assert.Equal("after", preserved.Attributes["name"]);
        Assert.Equal("keep", preserved.Attributes["hidden"]);
        Assert.Null(preserved.Attributes["nullable"]);
        Assert.False(preserved.Attributes.ContainsKey("removed"));
        Assert.False(preserved.Attributes.ContainsKey(idField));
        Assert.Equal(3, preserved.Attributes.Count);
        Assert.True(current.Attributes.ContainsKey(idField));
    }
}
