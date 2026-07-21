// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Stac.Services;

namespace Honua.Server.Tests.Features.Protocols.Stac.Services;

public sealed class StacItemIdWhereBuilderTests
{
    [Fact]
    public void GetCandidateFields_ReturnsCanonicalFieldsInPrecedenceOrder()
    {
        var fields = StacItemIdWhereBuilder.GetCandidateFields(CreateResource());

        Assert.Collection(
            fields,
            field => Assert.Equal("stac_id", field.Name),
            field => Assert.Equal("item_id", field.Name),
            field => Assert.Equal("id", field.Name));
    }

    [Fact]
    public void TryBuildFieldMatch_MultipleIds_UsesProviderNeutralInPredicate()
    {
        var field = CreateResource().SchemaFields.Single(candidate => candidate.Name == "stac_id");

        var built = StacItemIdWhereBuilder.TryBuildFieldMatch(field, ["123", "fallback-item"], out var where);

        Assert.True(built);
        Assert.Equal("stac_id IN ('123', 'fallback-item')", where);
    }

    [Fact]
    public void TryBuildFieldMatch_EscapesStringLiteral()
    {
        var field = new MetadataV2Field { Name = "stac_id", Type = MetadataV2FieldType.String };

        var built = StacItemIdWhereBuilder.TryBuildFieldMatch(field, ["it's-valid"], out var where);

        Assert.True(built);
        Assert.Equal("stac_id = 'it''s-valid'", where);
    }

    private static MetadataV2Resource CreateResource() => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "res-items", Name = "items" },
        Type = MetadataV2ResourceType.FeatureDataset,
        SchemaFields =
        [
            new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.BigInteger, SemanticRoles = ["id.primary"] },
            new MetadataV2Field { Name = "stac_id", Type = MetadataV2FieldType.String, Nullable = true },
            new MetadataV2Field { Name = "item_id", Type = MetadataV2FieldType.String, Nullable = true }
        ]
    };
}
