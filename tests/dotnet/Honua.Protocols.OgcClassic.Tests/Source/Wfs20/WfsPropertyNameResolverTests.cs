// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

[Protocol(TestProtocols.Wfs20)]
public sealed class WfsPropertyNameResolverTests
{
    private static readonly MetadataV2Resource _resource = new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "prefixed", Name = "prefixed" },
        Type = MetadataV2ResourceType.FeatureDataset,
        SchemaFields =
        [
            new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer },
            new MetadataV2Field { Name = "eo:cloud_cover", Type = MetadataV2FieldType.Double },
            new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry }
        ]
    };

    [Theory]
    [InlineData("eo:cloud_cover")]
    [InlineData("eo_x003A_cloud_cover")]
    [InlineData("honua:eo_x003A_cloud_cover")]
    [InlineData("/eo_x003A_cloud_cover")]
    public void Resolve_PrefixedFieldSpellings_ReturnsCanonicalName(string requestedName)
    {
        var resolved = WfsPropertyNameResolver.Resolve(
            _resource,
            requestedName,
            allowGeometryAlias: true);

        resolved.Should().Be("eo:cloud_cover");
    }

    [Fact]
    public void Resolve_UnknownEncodedField_ReturnsNull()
    {
        var resolved = WfsPropertyNameResolver.Resolve(
            _resource,
            "missing_x003A_field",
            allowGeometryAlias: true);

        resolved.Should().BeNull();
    }

    [Fact]
    public void Resolve_EncodedUnknownPrefix_DoesNotFallBackToLocalField()
    {
        var resource = _resource with
        {
            SchemaFields = [.. _resource.SchemaFields, new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }]
        };

        var resolved = WfsPropertyNameResolver.Resolve(
            resource,
            "missing_x003A_name",
            allowGeometryAlias: true);

        resolved.Should().BeNull();
    }

    [Fact]
    public void Resolve_AdvertisedEncoding_TakesPrecedenceOverEscapeLikeCanonicalName()
    {
        var resource = _resource with
        {
            SchemaFields =
            [
                .. _resource.SchemaFields,
                new MetadataV2Field { Name = "eo_x003A_cloud_cover", Type = MetadataV2FieldType.Double }
            ]
        };

        var resolved = WfsPropertyNameResolver.Resolve(
            resource,
            "eo_x003A_cloud_cover",
            allowGeometryAlias: true);

        resolved.Should().Be("eo:cloud_cover");
    }
}
