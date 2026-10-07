// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

// Audit platform-20261006 outcomes:
// SRV-DB-001 -> fixed; SRV_DB_001_PartialUpdate_PreservesEveryOmittedStoredAttribute.
// SRV-OGC-003 -> fixed; SRV_OGC_003_WfsUpdate_CarriesProviderReadStateToken.
// SRV-OGC-015 -> fixed; PatchConcurrencyTests.SRV_OGC_015_Delete_WithMaskedSnapshotAndMatchingIfMatch_Succeeds.

using System.Collections.Immutable;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

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
