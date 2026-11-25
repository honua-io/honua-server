// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureAttributePreservationTests
{
    [UnitTest]
    public void FeatureSerialization_PreservationHint_IsExcludedFromTheWireContract()
    {
        var feature = Feature.Create(42, null) with { PreservedAttributeNames = ["secret"] };

        var json = JsonSerializer.Serialize(feature, FeaturePreservationJsonContext.Default.Feature);
        var injected = json[..^1] + ",\"PreservedAttributeNames\":[\"injected\"]}";
        var deserialized = JsonSerializer.Deserialize(injected, FeaturePreservationJsonContext.Default.Feature);

        Assert.DoesNotContain(nameof(Feature.PreservedAttributeNames), json);
        Assert.True(deserialized.PreservedAttributeNames.IsDefaultOrEmpty);
    }

    [UnitTest]
    public void Replacement_PreservesOnlyNamedProtectedAttributes_AndReplacesOtherFields()
    {
        var current = Feature.Create(42, [1, 2],
            ImmutableDictionary<string, object?>.Empty
                .Add("code", "A")
                .Add("globalid", "stable-id")
                .Add("name", "before")
                .Add("description", "remove")
                .Add("undeclared", "remove"));
        var replacement = Feature.Create(42, null,
            ImmutableDictionary<string, object?>.Empty.Add("name", "after")) with
        {
            PreservedAttributeNames = ["CODE", "globalid"],
            ExplicitAttributeRemovals = ["code", "globalid", "description"]
        };

        var preserved = FeatureDataAccess.PreserveOmittedAttributes(replacement, current);

        Assert.Equal("after", preserved.Attributes["name"]);
        Assert.Equal("A", preserved.Attributes["code"]);
        Assert.Equal("stable-id", preserved.Attributes["globalid"]);
        Assert.False(preserved.Attributes.ContainsKey("description"));
        Assert.False(preserved.Attributes.ContainsKey("undeclared"));
        Assert.Null(preserved.Geometry);
        Assert.Equal(3, preserved.Attributes.Count);
    }

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

    [UnitTest]
    public void ApplyMaskedFieldsToEditBatch_MergesMaskedFieldsIntoUpdateAndOrderedUpdatePreservedNames()
    {
        var unorderedUpdate = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty.Add("name", "a")) with
        {
            PreservedAttributeNames = ["globalid"],
            PreserveOmittedMaskedAttributes = false
        };
        var orderedUpdate = Feature.Create(2, null, ImmutableDictionary<string, object?>.Empty.Add("name", "b")) with
        {
            PreserveOmittedMaskedAttributes = false
        };
        var batch = FeatureEditBatch.Create(
            updates: [unorderedUpdate],
            operations: [FeatureEditOperation.Update(orderedUpdate)],
            preconditions: [new FeatureEditPrecondition { ObjectId = 1, ExpectedStateToken = "tok" }]);

        var prepared = Honua.Db.Postgres.Features.FeatureStore.PostgresFeatureStoreRefactored
            .ApplyMaskedFieldsToEditBatch(batch, ["ssn", "globalid"]);

        Assert.Equal(["globalid", "ssn"], prepared.Updates[0].PreservedAttributeNames.OrderBy(static n => n, StringComparer.Ordinal));
        Assert.Equal(["globalid", "ssn"], prepared.Operations[0].Feature!.Value.PreservedAttributeNames.OrderBy(static n => n, StringComparer.Ordinal));
        Assert.Equal(["globalid", "ssn"], prepared.Preconditions[0].MaskedFields.OrderBy(static n => n, StringComparer.Ordinal));

        // Replacement update with PreserveOmittedMaskedAttributes = false still preserves masked fields.
        var current = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("name", "old")
            .Add("ssn", "123-45-6789")
            .Add("globalid", "gid-1")
            .Add("notes", "drop-me"));
        var merged = FeatureDataAccess.PreserveOmittedAttributes(prepared.Updates[0], current);
        Assert.Equal("a", merged.Attributes["name"]);
        Assert.Equal("123-45-6789", merged.Attributes["ssn"]);
        Assert.Equal("gid-1", merged.Attributes["globalid"]);
        Assert.False(merged.Attributes.ContainsKey("notes"));
    }

    [UnitTest]
    public void ValidatePreconditionAgainstRow_MismatchedToken_ThrowsWithMaskedConflictFeature()
    {
        var current = Feature.Create(7, null, ImmutableDictionary<string, object?>.Empty
            .Add("name", "visible")
            .Add("secret", "hidden"));
        var precondition = new FeatureEditPrecondition
        {
            ObjectId = 7,
            ExpectedStateToken = "stale-token",
            MaskedFields = ["secret"]
        };

        var ex = Assert.Throws<FeatureEditPreconditionFailedException>(() =>
            FeatureDataAccess.ValidatePreconditionAgainstRow(1, 7, current, precondition));

        Assert.Equal(7, ex.ObjectId);
        Assert.Equal("visible", ex.CurrentFeature.Attributes["name"]);
        Assert.False(ex.CurrentFeature.Attributes.ContainsKey("secret"));
    }
}

[JsonSerializable(typeof(Feature))]
internal sealed partial class FeaturePreservationJsonContext : JsonSerializerContext
{
}
