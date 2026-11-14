// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.PairedPublications;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

public sealed class PairedPublicationFieldsTests
{
    [UnitTest]
    public void Resolve_HiddenFieldWithUnportableName_IsLeftOutOfTheDescriptor()
    {
        var fields = PairedPublicationFields.Resolve(
            Resource(Field("objectid", MetadataV2FieldType.Integer), Field("tmk", MetadataV2FieldType.String),
                Field("Owner Name", MetadataV2FieldType.String) with { Hidden = true }),
            "tmk");

        fields.Declared.Select(field => field.Name).Should().Equal("objectid", "tmk");
        fields.Hidden.Should().Equal("Owner Name");
    }

    [UnitTest]
    public void Resolve_HiddenField_DeclaresOnlyNameTypeAndVisibility()
    {
        var fields = PairedPublicationFields.Resolve(
            Resource(Field("objectid", MetadataV2FieldType.Integer), Field("tmk", MetadataV2FieldType.String),
                Field("owner", MetadataV2FieldType.String) with { Hidden = true, Alias = "Owner", Description = "Private" }),
            "tmk");

        var owner = fields.Declared.Single(field => field.Name == "owner");
        owner.Public.Should().BeFalse();
        owner.Title.Should().BeNull();
        owner.Description.Should().BeNull();
        fields.TileFields.Should().Equal("objectid", "tmk");
    }

    [UnitTheory]
    [InlineData("geometry")]
    [InlineData("bbox")]
    public void Resolve_VisibleFieldNamedLikeAGeoParquetColumn_IsRefused(string name)
    {
        var resolve = () => PairedPublicationFields.Resolve(
            Resource(Field("objectid", MetadataV2FieldType.Integer), Field("tmk", MetadataV2FieldType.String),
                Field(name, MetadataV2FieldType.String)),
            "tmk");

        resolve.Should().Throw<PairedPublicationException>().WithMessage($"*'{name}'*");
    }

    [UnitTest]
    public void Resolve_HiddenObjectId_IsRefused()
    {
        var resolve = () => PairedPublicationFields.Resolve(
            Resource(Field("objectid", MetadataV2FieldType.Integer) with { Hidden = true }, Field("tmk", MetadataV2FieldType.String)),
            "tmk");

        resolve.Should().Throw<PairedPublicationException>().WithMessage("*'objectid' is hidden*");
    }

    [UnitTest]
    public void Resolve_NumericIdentity_IsRefused()
    {
        var resolve = () => PairedPublicationFields.Resolve(
            Resource(Field("objectid", MetadataV2FieldType.Integer), Field("tmk", MetadataV2FieldType.Integer)),
            "tmk");

        resolve.Should().Throw<PairedPublicationException>().WithMessage("*Identity field 'tmk'*");
    }

    private static MetadataV2Field Field(string name, MetadataV2FieldType type) => new() { Name = name, Type = type, Nullable = true };

    private static MetadataV2Resource Resource(params MetadataV2Field[] fields) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "parcels", Name = "parcels" },
        SchemaFields =
        [
            .. fields,
            new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = true, SemanticRoles = ["geometry.primary"] }
        ],
        Spatial = new MetadataV2ResourceSpatial
        {
            SpatialReference = MetadataV2SpatialReference.Wgs84,
            GeometryType = MetadataV2GeometryType.Polygon,
            PrimaryGeometryField = "shape"
        }
    };
}
