// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Federation.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Federation;

public sealed class FeatureOrderingTests
{
    [Theory]
    [InlineData(true, 2, 3)]
    [InlineData(false, 1, 3)]
    public void Apply_ResolvesSchemaFieldCaseAndSortsBeforePaging(bool ascending, long firstId, long secondId)
    {
        var resource = Resource("DisplayName", "OBJECTID");
        var features = ImmutableArray.Create(
            Feature(1, ("DisplayName", "Zulu")),
            Feature(3, ("DisplayName", "Bravo")),
            Feature(2, ("DisplayName", "Alpha")));
        var orderBy = ImmutableArray.Create(new OrderByClause("displayname", ascending));

        var page = FeatureOrdering.Apply(features, orderBy, resource).Take(2).ToArray();

        Assert.Equal([firstId, secondId], page.Select(feature => feature.Id));
    }

    [UnitTest]
    public void Apply_PrimaryIdFieldUsesFeatureIdAndNotAttributeValue()
    {
        var resource = Resource("name", "OBJECTID");
        var features = ImmutableArray.Create(
            Feature(20, ("OBJECTID", 1)),
            Feature(10, ("OBJECTID", 999)));

        var ordered = FeatureOrdering.Apply(
            features,
            ImmutableArray.Create(OrderByClause.Asc("objectid")),
            resource);

        Assert.Equal([10L, 20L], ordered.Select(feature => feature.Id));
    }

    [UnitTest]
    public void Compare_ResolvesRequestedFieldAgainstEachResourceSchema()
    {
        var left = Feature(1, ("NAME", "Zulu"));
        var right = Feature(2, ("Name", "Alpha"));
        var clauses = ImmutableArray.Create(OrderByClause.Asc("name"));

        var comparison = FeatureOrdering.Compare(
            left,
            Resource("NAME", "OID"),
            right,
            Resource("Name", "ObjectId"),
            clauses);

        Assert.True(comparison > 0);
    }

    private static MetadataV2Resource Resource(string nameField, string idField) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = $"resource-{nameField}", Name = nameField },
        Type = MetadataV2ResourceType.FeatureDataset,
        SchemaFields =
        [
            new MetadataV2Field { Name = idField, Type = MetadataV2FieldType.BigInteger, SemanticRoles = ["id.primary"] },
            new MetadataV2Field { Name = nameField, Type = MetadataV2FieldType.String }
        ]
    };

    private static Feature Feature(long id, params (string Key, object? Value)[] attributes) => new()
    {
        Id = id,
        Geometry = null,
        Attributes = attributes.ToImmutableDictionary(pair => pair.Key, pair => pair.Value)
    };
}
