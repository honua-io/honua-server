// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

// Audit platform-20261006 outcomes:
// SRV-DB-001 -> fixed; SRV_DB_001_PartialUpdate_PreservesEveryOmittedStoredAttribute.
// SRV-OGC-003 -> fixed; SRV_OGC_003_WfsUpdate_CarriesProviderReadStateToken.
// SRV-OGC-015 -> fixed; PatchConcurrencyTests.SRV_OGC_015_Delete_WithMaskedSnapshotAndMatchingIfMatch_Succeeds.

using System.Collections.Immutable;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests;

public sealed class FieldMaskedEditRegressionTests
{
    [UnitTest]
    public void SRV_DB_001_PartialUpdate_PreservesEveryOmittedStoredAttribute()
    {
        var stored = Feature.Create(
            42,
            null,
            ImmutableDictionary<string, object?>.Empty
                .Add("name", "before")
                .Add("secret", "preserve me"));
        var update = Feature.Create(
            42,
            null,
            ImmutableDictionary<string, object?>.Empty.Add("name", "after")) with
        {
            PreserveOmittedMaskedAttributes = true
        };

        var merged = FeatureDataAccess.PreserveOmittedAttributes(update, stored);

        Assert.Equal("after", merged.Attributes["name"]);
        Assert.Equal("preserve me", merged.Attributes["secret"]);
    }

    [UnitTest]
    public async Task SRV_DB_001_TwoPartialUpdates_KeepOmittedHiddenField()
    {
        var stored = new[]
        {
            Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty.Add("name", "a").Add("secret", "keep-1")),
            Feature.Create(2, null, ImmutableDictionary<string, object?>.Empty.Add("name", "b").Add("secret", "keep-2"))
        };
        var partials = new[]
        {
            FeatureServerEditsHandler.MarkPartialGeoServicesUpdate(
                Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty.Add("name", "a2")),
                isPartialUpdate: true),
            FeatureServerEditsHandler.MarkPartialGeoServicesUpdate(
                Feature.Create(2, null, ImmutableDictionary<string, object?>.Empty.Add("name", "b2")),
                isPartialUpdate: true)
        };
        var adapter = new GeoServicesEditParameterAdapter(NullLogger<GeoServicesEditParameterAdapter>.Instance);
        var converted = await adapter.ConvertAsync(
            new GeoServicesEditRequest
            {
                Updates = ImmutableArray.Create(partials[0], partials[1])
            },
            new MetadataV2Resource());

        Assert.True(converted.IsSuccess);
        var batch = new EditProcessor(NullLogger<EditProcessor>.Instance)
            .ToFeatureEditBatch(converted.EditRequest!.Value, new MetadataV2Resource());
        Assert.Equal(2, batch.Updates.Length);
        Assert.Empty(batch.Preconditions);
        for (var i = 0; i < stored.Length; i++)
        {
            Assert.True(batch.Updates[i].PreserveOmittedMaskedAttributes);
            var merged = FeatureDataAccess.PreserveOmittedAttributes(batch.Updates[i], stored[i]);
            Assert.Equal(partials[i].Attributes["name"], merged.Attributes["name"]);
            Assert.Equal(stored[i].Attributes["secret"], merged.Attributes["secret"]);
        }

        var replacement = FeatureServerEditsHandler.MarkPartialGeoServicesUpdate(
            Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty.Add("name", "replaced")),
            isPartialUpdate: false);
        var replaced = await adapter.ConvertAsync(
            new GeoServicesEditRequest { Updates = ImmutableArray.Create(replacement) },
            new MetadataV2Resource());
        var replacedBatch = new EditProcessor(NullLogger<EditProcessor>.Instance)
            .ToFeatureEditBatch(replaced.EditRequest!.Value, new MetadataV2Resource());
        Assert.False(replacedBatch.Updates[0].PreserveOmittedMaskedAttributes);
        var replacedRow = FeatureDataAccess.PreserveOmittedAttributes(replacedBatch.Updates[0], stored[0]);
        Assert.False(replacedRow.Attributes.ContainsKey("secret"));
    }

    [UnitTest]
    public async Task SRV_DB_001_SingleUpdatePrecondition_UsesFullRowTokenNotMaskedSnapshot()
    {
        var full = Feature.Create(
            7,
            null,
            ImmutableDictionary<string, object?>.Empty.Add("name", "visible").Add("secret", "hidden"));
        var fullToken = FeatureStateToken.Compute(full);
        var masked = full with { Attributes = full.Attributes.Remove("secret") };
        var maskedToken = FeatureStateToken.Compute(masked);
        Assert.NotEqual(fullToken, maskedToken);

        var editSnapshot = masked with { ReadStateToken = fullToken };
        var reader = Substitute.For<IFeatureReader>();
        reader.GetAsync(4, 7, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Feature?>(editSnapshot));

        var precondition = await FeatureServerEditsHandler.TryCreateGuardedUpdatePreconditionAsync(
            reader,
            4,
            7,
            CancellationToken.None);

        Assert.NotNull(precondition);
        Assert.Equal(7, precondition.Value.ObjectId);
        Assert.Equal(fullToken, precondition.Value.ExpectedStateToken);
        Assert.NotEqual(maskedToken, precondition.Value.ExpectedStateToken);
        Assert.Equal(FeatureStateToken.FromReadSnapshot(editSnapshot), precondition.Value.ExpectedStateToken);
        await reader.Received(1).GetAsync(4, 7, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task SRV_OGC_003_WfsUpdate_CarriesProviderReadStateToken()
    {
        var snapshot = Feature.Create(
            42,
            null,
            ImmutableDictionary<string, object?>.Empty.Add("status", "active")) with
        {
            ReadStateToken = "provider-full-row-token"
        };
        var adapter = new Wfs20EditParameterAdapter(NullLogger<Wfs20EditParameterAdapter>.Instance);

        var converted = await adapter.ConvertAsync(new Wfs20EditRequest
        {
            Operations = [new(FeatureEditOperation.Update(snapshot))],
            RollbackOnFailure = true
        });

        Assert.True(converted.IsSuccess);
        var operations = converted.EditRequest!.Value.Operations!.Value;
        var constraint = Assert.Single(operations).Feature!.Value.Constraints;
        Assert.Equal("provider-full-row-token", constraint!.Value.ExpectedStateToken);
    }

    [UnitTest]
    public async Task WfsUpdateAndReplace_PreserveDistinctModesAndSnapshotPreconditions()
    {
        var update = Feature.Create(42, null,
            ImmutableDictionary<string, object?>.Empty.Add("name", "updated")) with
        {
            ReadStateToken = "update-snapshot-token"
        };
        var replacement = Feature.Create(43, null,
            ImmutableDictionary<string, object?>.Empty.Add("name", "replaced")) with
        {
            ReadStateToken = "replace-snapshot-token"
        };
        var adapter = new Wfs20EditParameterAdapter(NullLogger<Wfs20EditParameterAdapter>.Instance);
        var converted = await adapter.ConvertAsync(new Wfs20EditRequest
        {
            Operations =
            [
                new(FeatureEditOperation.Update(update)),
                new(FeatureEditOperation.Update(replacement), EditUpdateMode.Replace)
            ]
        });

        Assert.True(converted.IsSuccess);
        var operations = converted.EditRequest!.Value.Operations!.Value;
        Assert.Equal(EditUpdateMode.Merge, operations[0].Feature!.Value.UpdateMode);
        Assert.Equal(EditUpdateMode.Replace, operations[1].Feature!.Value.UpdateMode);
        var processor = new EditProcessor(NullLogger<EditProcessor>.Instance);
        var batch = processor.ToFeatureEditBatch(converted.EditRequest!.Value, new MetadataV2Resource());
        Assert.True(batch.Operations[0].Feature!.Value.PreserveOmittedMaskedAttributes);
        Assert.False(batch.Operations[1].Feature!.Value.PreserveOmittedMaskedAttributes);
        Assert.Equal("update-snapshot-token", batch.Preconditions.Single(p => p.ObjectId == 42).ExpectedStateToken);
        Assert.Equal("replace-snapshot-token", batch.Preconditions.Single(p => p.ObjectId == 43).ExpectedStateToken);
    }
}
