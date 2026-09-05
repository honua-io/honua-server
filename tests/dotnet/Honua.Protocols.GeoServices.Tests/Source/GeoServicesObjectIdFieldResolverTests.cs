// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

public sealed class GeoServicesObjectIdFieldResolverTests
{
    [Theory]
    [InlineData("id")]
    [InlineData("fid")]
    [InlineData("objectid")]
    public void ResolveObjectIdField_NumericPrimaryKey_PrecedesConventionalAttribute(string attribute)
    {
        var primary = new MetadataV2Field
        {
            Name = "gid",
            Type = MetadataV2FieldType.BigInteger,
            SemanticRoles = ["id.primary"]
        };
        var resource = new MetadataV2Resource
        {
            SchemaFields = [new() { Name = attribute, Type = MetadataV2FieldType.Integer }, primary]
        };

        GeoServicesObjectIdFieldResolver.ResolveObjectIdField(resource).Should().BeSameAs(primary);
        GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource).Should().Be("gid");
    }

    [Theory]
    [InlineData("id")]
    [InlineData("fid")]
    [InlineData("population")]
    public void ResolveObjectIdField_StringPrimaryKey_DoesNotPromoteNumericAttribute(string attribute)
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new() { Name = "uuid", Type = MetadataV2FieldType.String, SemanticRoles = ["id.primary"] },
                new() { Name = attribute, Type = MetadataV2FieldType.Integer }
            ]
        };

        GeoServicesObjectIdFieldResolver.ResolveObjectIdField(resource).Should().BeNull();
    }

    [Fact]
    public void ResolveObjectIdField_NoKey_DoesNotPromoteArbitraryInteger()
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields = [new() { Name = "population", Type = MetadataV2FieldType.Integer }]
        };

        GeoServicesObjectIdFieldResolver.ResolveObjectIdField(resource).Should().BeNull();
    }
}
