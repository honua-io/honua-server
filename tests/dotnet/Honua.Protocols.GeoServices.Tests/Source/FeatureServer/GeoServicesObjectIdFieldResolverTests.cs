// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed class GeoServicesObjectIdFieldResolverTests
{
    [UnitTest]
    public void ResolveObjectIdField_PrefersSemanticPrimaryOverNonKeyIdColumn()
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.Integer },
                new MetadataV2Field { Name = "parcel_key", Type = MetadataV2FieldType.BigInteger, SemanticRoles = ["id.primary"] },
                new MetadataV2Field { Name = "rank", Type = MetadataV2FieldType.Integer }
            ]
        };

        GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource)
            .Should().Be("parcel_key");
    }

    [UnitTest]
    public void ResolveObjectIdField_DoesNotFallBackToArbitraryIntegerColumn()
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields = [new MetadataV2Field { Name = "rank", Type = MetadataV2FieldType.Integer }]
        };

        GeoServicesObjectIdFieldResolver.ResolveObjectIdField(resource).Should().BeNull();
        GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource).Should().Be("objectid");
    }
}
