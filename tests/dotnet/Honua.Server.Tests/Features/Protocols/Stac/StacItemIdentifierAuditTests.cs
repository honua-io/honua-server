// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Filtering;
using Honua.Protocols.Stac.Services;

namespace Honua.Server.Tests.Features.Protocols.Stac;

public sealed class StacItemIdentifierAuditTests
{
    [Fact]
    public void SRV_OGC_005_NonUniqueConventionNamedFieldFallsBackToFeatureIdentifier()
    {
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-items", Name = "items" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "objectid",
                    Type = MetadataV2FieldType.BigInteger,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.Integer }
            ]
        };
        var feature = Feature.Create(
            42,
            geometry: null,
            ImmutableDictionary<string, object?>.Empty.Add("id", 0));

        Assert.Equal("42", StacMappingService.ResolveItemId(feature, resource));
        Assert.Collection(
            StacItemIdWhereBuilder.GetCandidateFields(resource),
            field => Assert.Equal("objectid", field.Name));
    }

    [Fact]
    public void SRV_OGC_018_IdFilterUsesTheIdentifierSerializedByStac()
    {
        var resource = CreateResource();
        var expression = new BinaryExpression(
            new PropertyReference("id"),
            BinaryOperator.Equal,
            new Literal("scene-42", LiteralType.Text));

        var rewritten = Assert.IsType<BinaryExpression>(
            Cql2FilterProcessor.RewriteStacCoreQueryables(expression, resource, "items"));

        Assert.Equal("stac_id", Assert.IsType<PropertyReference>(rewritten.Left).PropertyName);
    }

    private static MetadataV2Resource CreateResource() => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "res-items", Name = "items" },
        Type = MetadataV2ResourceType.FeatureDataset,
        SchemaFields =
        [
            new MetadataV2Field
            {
                Name = "stac_id",
                Type = MetadataV2FieldType.String,
                SemanticRoles = ["id.primary"]
            },
            new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.Integer }
        ]
    };
}
