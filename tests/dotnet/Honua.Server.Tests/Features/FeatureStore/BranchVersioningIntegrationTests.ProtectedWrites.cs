// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.FeatureStore;

public sealed partial class BranchVersioningIntegrationTests
{
    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Operation(Operations.ApplyEdits)]
    public async Task VersionedReplacement_PreservesProtectedFieldsFromCurrentBranch(bool ordered, bool existingOverlay)
    {
        var store = CreateFeatureStore();
        var original = await store.CreateAsync(PointsLayerId,
            BuildFeatureWithFields(1, 1, ("name", "base"), ("code", "A"), ("globalid", "stable-id"),
                ("description", "remove"), ("undeclared", "remove")), CancellationToken.None);
        var version = await CreateVersionManager().CreateAsync(
            new CreateVersionRequest($"Protected{Guid.NewGuid():N}", "sde", VersionAccess.Public), CancellationToken.None);
        var context = VersionContext.ForVersion(version);
        if (existingOverlay)
        {
            var overlay = original with { Attributes = original.Attributes.SetItem("code", "B") };
            (await store.ApplyEditsAsync(PointsLayerId,
                FeatureEditBatch.Create(updates: [overlay], versionContext: context), CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        var replacement = Feature.Create(original.Id, null,
            ImmutableDictionary<string, object?>.Empty.Add("name", "after")) with
        {
            PreservedAttributeNames = ["code", "globalid"],
            ExplicitAttributeRemovals = ["code"]
        };
        var batch = ordered
            ? FeatureEditBatch.Create(operations: [FeatureEditOperation.Update(replacement)], versionContext: context)
            : FeatureEditBatch.Create(updates: [replacement], versionContext: context);

        (await store.ApplyEditsAsync(PointsLayerId, batch, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var result = await store.QueryAsync(PointsLayerId,
            new FeatureQuery { ObjectIds = [original.Id], VersionContext = context }, CancellationToken.None);
        var stored = result.Items.Should().ContainSingle().Which;
        FieldValue(stored, "code").Should().Be(existingOverlay ? "B" : "A");
        FieldValue(stored, "globalid").Should().Be("stable-id");
        stored.Attributes.Should().NotContainKey("description").And.NotContainKey("undeclared");
        stored.Geometry.Should().BeNull();
        FieldValue((await store.GetAsync(PointsLayerId, original.Id, CancellationToken.None))!.Value, "code").Should().Be("A");
    }

    [IntegrationTheory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [Operation(Operations.ApplyEdits)]
    public async Task VersionedReplacement_HiddenRlsRow_RejectsMutationAgainstEffectiveBranch(
        bool ordered, bool existingOverlay, bool delete)
    {
        var (store, dataAccess) = CreateFeatureStoreWithDataAccess();
        var original = await store.CreateAsync(PointsLayerId,
            BuildFeatureWithFields(1, 1, ("name", "private"), ("region", existingOverlay ? "north" : "south")), CancellationToken.None);
        var version = await CreateVersionManager().CreateAsync(
            new CreateVersionRequest($"Policy{Guid.NewGuid():N}", "sde", VersionAccess.Public), CancellationToken.None);
        var context = VersionContext.ForVersion(version);
        if (existingOverlay)
        {
            var hidden = original with { Attributes = original.Attributes.SetItem("region", "south") };
            (await store.ApplyEditsAsync(PointsLayerId,
                FeatureEditBatch.Create(updates: [hidden], versionContext: context), CancellationToken.None)).IsSuccess.Should().BeTrue();
        }
        var update = original with { Attributes = original.Attributes.SetItem("region", "north").SetItem("name", "stolen") };
        var operation = delete ? FeatureEditOperation.Delete(original.Id) : FeatureEditOperation.Update(update);
        var batch = (ordered
            ? FeatureEditBatch.Create(operations: [operation], versionContext: context)
            : FeatureEditBatch.Create(updates: delete ? [] : [update], deletes: delete ? [original.Id] : [], versionContext: context)) with
        {
            RowSecurityWriteCheck = new SqlFragment("(\"attributes\" ->> 'region') = @p0", ["north"])
        };

        var result = await dataAccess.ApplyEditsAsync(PointsLayerId, batch, CancellationToken.None);

        var outcome = (delete ? result.DeleteResults : result.UpdateResults).Should().ContainSingle().Which;
        outcome.IsSuccess.Should().BeFalse();
        outcome.ErrorCode.Should().Be(EditOperationResult.NotPermittedErrorCode);
        var current = (await store.QueryAsync(PointsLayerId,
            new FeatureQuery { ObjectIds = [original.Id], VersionContext = context }, CancellationToken.None)).Items.Should().ContainSingle().Which;
        FieldValue(current, "region").Should().Be("south");
        FieldValue(current, "name").Should().Be("private");
    }

}
