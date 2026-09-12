// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.FeatureStore.Domain;

/// <summary>
/// Covers <see cref="FeatureStorageMapping.SupportsManagedWrites"/>, the predicate the write
/// surfaces consult before applying a mutation through the managed feature store
/// (honua-server#4707).
/// </summary>
[Protocol(ProtocolNames.TestQuality)]
public sealed class FeatureStorageMappingManagedWriteTests
{
    [UnitTest]
    [Operation(Operations.Create)]
    public void SupportsManagedWrites_MappingWithNoSourceBackedOption_IsWritable()
    {
        var mapping = new FeatureStorageMapping("parcels", SchemaName: "public");

        mapping.IsSourceBacked.Should().BeFalse();
        mapping.SupportsManagedWrites.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void SupportsManagedWrites_SourceBackedOntoSharedFeaturesTable_IsWritable()
    {
        // The shape the publish path produces for a layer stored in the managed feature
        // store: source-backed onto the shared 'features' table, attributes in JSONB and
        // rows separated by the layer discriminator. Reads and managed writes both land on
        // those rows, so the write is readable back.
        var mapping = new FeatureStorageMapping(
            TableName: "features",
            SchemaName: "honua",
            AttributesColumn: "attributes",
            LayerDiscriminatorColumn: "layer_id",
            LayerDiscriminatorValue: 7,
            ProviderOptions: new Dictionary<string, string>
            {
                [FeatureStorageMapping.SourceBackedOption] = "true"
            });

        mapping.IsSourceBacked.Should().BeTrue();
        mapping.SupportsManagedWrites.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void SupportsManagedWrites_SourceBackedOntoExternalTable_IsNotWritable()
    {
        // The shape the publish path produces for a layer published over a user table: the
        // serving protocols read public.parcels while the managed writer would write the
        // shared features table, so an accepted write could never be read back.
        var mapping = new FeatureStorageMapping(
            TableName: "parcels",
            SchemaName: "public",
            PrimaryKeyColumn: "id",
            GeometryColumn: "geom",
            ProviderOptions: new Dictionary<string, string>
            {
                [FeatureStorageMapping.SourceBackedOption] = "true"
            });

        mapping.IsSourceBacked.Should().BeTrue();
        mapping.SupportsManagedWrites.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void SupportsManagedWrites_SharedTableNameComparisonIgnoresCase()
    {
        var mapping = new FeatureStorageMapping(
            TableName: "FEATURES",
            SchemaName: "Honua",
            ProviderOptions: new Dictionary<string, string>
            {
                [FeatureStorageMapping.SourceBackedOption] = "true"
            });

        mapping.SupportsManagedWrites.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void SupportsManagedWrites_NonBooleanSourceBackedOption_IsWritable()
    {
        // An unparseable option is not a source-backed declaration, so the mapping keeps the
        // managed-store default rather than becoming silently unwritable.
        var mapping = new FeatureStorageMapping(
            TableName: "parcels",
            SchemaName: "public",
            ProviderOptions: new Dictionary<string, string>
            {
                [FeatureStorageMapping.SourceBackedOption] = "yes"
            });

        mapping.IsSourceBacked.Should().BeFalse();
        mapping.SupportsManagedWrites.Should().BeTrue();
    }
}
