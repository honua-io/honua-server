// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

// Audit platform-20261006 outcomes:
// SRV-DB-001 -> fixed; SRV_DB_001_PartialUpdate_PreservesEveryOmittedStoredAttribute.
// SRV-OGC-003 -> fixed; SRV_OGC_003_WfsUpdate_CarriesProviderReadStateToken.
// SRV-OGC-015 -> fixed; PatchConcurrencyTests.SRV_OGC_015_Delete_WithMaskedSnapshotAndMatchingIfMatch_Succeeds.

using System.Collections.Immutable;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests;

public sealed class FieldMaskedEditRegressionTests
{
    [Fact]
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

    [Fact]
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
            Operations = [FeatureEditOperation.Update(snapshot)],
            RollbackOnFailure = true
        });

        Assert.True(converted.IsSuccess);
        var operations = converted.EditRequest!.Value.Operations!.Value;
        var constraint = Assert.Single(operations).Feature!.Value.Constraints;
        Assert.Equal("provider-full-row-token", constraint!.Value.ExpectedStateToken);
    }
}
