// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Filtering;
using Honua.Infrastructure.Stac;
using Honua.Protocols.Stac;
using Honua.Protocols.Stac.Models;
using Honua.Protocols.Stac.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Stac.Services;

public sealed class StacItemIdWhereBuilderTests
{
    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public void GetCandidateFields_ReturnsCanonicalFieldsInPrecedenceOrder()
    {
        var fields = StacItemIdWhereBuilder.GetCandidateFields(CreateResource());

        Assert.Collection(
            fields,
            field => Assert.Equal("id", field.Name));
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public void TryBuildFieldMatch_MultipleIds_UsesProviderNeutralInPredicate()
    {
        var field = CreateResource().SchemaFields.Single(candidate => candidate.Name == "stac_id");

        var built = StacItemIdWhereBuilder.TryBuildFieldMatch(field, ["123", "fallback-item"], out var where);

        Assert.True(built);
        Assert.Equal("stac_id IN ('123', 'fallback-item')", where);
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public void TryBuildFieldMatch_EscapesStringLiteral()
    {
        var field = new MetadataV2Field { Name = "stac_id", Type = MetadataV2FieldType.String };

        var built = StacItemIdWhereBuilder.TryBuildFieldMatch(field, ["it's-valid"], out var where);

        Assert.True(built);
        Assert.Equal("stac_id = 'it''s-valid'", where);
    }

    [UnitTest]
    public void GetCandidateFields_UniqueCanonicalRole_IncludesPrimaryFallbackAndSkipsNonUniqueNames()
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, SemanticRoles = ["id.primary"] },
                new MetadataV2Field { Name = "stac_id", Type = MetadataV2FieldType.String, SemanticRoles = ["id.global"] },
                new MetadataV2Field { Name = "item_id", Type = MetadataV2FieldType.String }
            ]
        };

        var fields = StacItemIdWhereBuilder.GetCandidateFields(resource);

        Assert.Collection(
            fields,
            field => Assert.Equal("stac_id", field.Name),
            field => Assert.Equal("objectid", field.Name));
    }

    [UnitTest]
    public void GetCandidateFields_NonUniqueIdColumn_UsesObjectId()
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "Id", Type = MetadataV2FieldType.Integer },
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, SemanticRoles = ["id.primary"] }
            ]
        };

        var fields = StacItemIdWhereBuilder.GetCandidateFields(resource);

        Assert.Collection(fields, field => Assert.Equal("objectid", field.Name));
    }

    [UnitTest]
    public void GetCandidateFields_ExplicitIdField_UsesThatSchemaField()
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "item_code", Type = MetadataV2FieldType.String },
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, SemanticRoles = ["id.primary"] }
            ],
            Extensions = new Dictionary<string, JsonElement>
            {
                ["stac"] = JsonSerializer.SerializeToElement(new { idField = "item_code" })
            }
        };

        var fields = StacItemIdWhereBuilder.GetCandidateFields(resource);

        Assert.Collection(
            fields,
            field => Assert.Equal("item_code", field.Name),
            field => Assert.Equal("objectid", field.Name));
        Assert.Equal(
            "attributes->>'item_code' = @p0",
            StacItemIdWhereBuilder.BuildUnboundAttributePredicate(resource));
    }

    [UnitTest]
    public void ResolveItemId_AttributeBag_UsesCanonicalValueWhenItIsNotASchemaColumn()
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields = [new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer }]
        };
        var feature = Feature.Create(
            7,
            geometry: null,
            ImmutableDictionary<string, object?>.Empty.Add("id", "stac-item-alpha"));

        Assert.Equal("stac-item-alpha", StacMappingService.ResolveItemId(feature, resource));
    }

    [UnitTest]
    public void ResolveItemId_NonUniqueIdColumn_UsesObjectIdInsteadOfRepeatedAttribute()
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "Id", Type = MetadataV2FieldType.Integer },
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, SemanticRoles = ["id.primary"] }
            ]
        };
        var feature = Feature.Create(
            42,
            geometry: null,
            ImmutableDictionary<string, object?>.Empty.Add("Id", 0));

        Assert.Equal("42", StacMappingService.ResolveItemId(feature, resource));
    }

    [UnitTest]
    public void RewriteStacCoreQueryables_StringIdentity_KeepsStringLiteral()
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "stac_id", Type = MetadataV2FieldType.String, SemanticRoles = ["id.global"] },
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger, SemanticRoles = ["id.primary"] }
            ]
        };
        var expression = new BinaryExpression(
            new PropertyReference("id"),
            BinaryOperator.Equal,
            new Literal("stac-item-alpha", LiteralType.Text));

        var rewritten = Cql2FilterProcessor.RewriteStacCoreQueryables(expression, resource, "places");

        var binary = Assert.IsType<BinaryExpression>(rewritten);
        Assert.Equal("stac_id", Assert.IsType<PropertyReference>(binary.Left).PropertyName);
        var literal = Assert.IsType<Literal>(binary.Right);
        Assert.Equal(LiteralType.Text, literal.Type);
        Assert.Equal("stac-item-alpha", literal.Value);
    }

    [UnitTest]
    public void RewriteStacCoreQueryables_ObjectIdFallback_CoercesNumericString()
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "Id", Type = MetadataV2FieldType.String },
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger, SemanticRoles = ["id.primary"] }
            ]
        };
        var expression = new BinaryExpression(
            new PropertyReference("id"),
            BinaryOperator.Equal,
            new Literal("15", LiteralType.Text));

        var rewritten = Cql2FilterProcessor.RewriteStacCoreQueryables(expression, resource, "places");

        var binary = Assert.IsType<BinaryExpression>(rewritten);
        Assert.Equal("objectid", Assert.IsType<PropertyReference>(binary.Left).PropertyName);
        var literal = Assert.IsType<Literal>(binary.Right);
        Assert.Equal(LiteralType.Number, literal.Type);
        Assert.Equal(15L, literal.Value);
    }

    [UnitTest]
    public void TryBuildSortOrder_Id_UsesResolvedIdentityField()
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "Id", Type = MetadataV2FieldType.Integer },
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, SemanticRoles = ["id.primary"] }
            ]
        };

        var built = SearchEndpoints.TryBuildSortOrder(
            resource,
            [new StacSortDefinition { Field = "id", Direction = "asc" }],
            out var orderBy,
            out var error);

        Assert.True(built);
        Assert.Null(error);
        Assert.Equal("objectid", orderBy[0].Field);
    }

    [UnitTest]
    public void BuildSearchQuery_UsesNextCursorInsteadOfToken()
    {
        var query = SearchEndpoints.BuildSearchQuery(10, 10, new StacSearchRequest(), true);

        Assert.Contains("next=offset%3A10", query, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", query, StringComparison.Ordinal);
    }

    [UnitTest]
    public void ContinuationToken_RecognizesOnlyNonNegativeOffsetCursors()
    {
        Assert.True(StacContinuationTokens.IsCursor("offset:10"));
        Assert.False(StacContinuationTokens.IsCursor("offset:-1"));
        Assert.False(StacContinuationTokens.IsCursor("portal-token"));
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
