// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Exports;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

public sealed class ExportFeatureCaptureTests
{
    [UnitTest]
    public async Task Read_DuplicateIdentityValues_IsRefused()
    {
        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "parcels", Name = "parcels" },
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = true },
                new MetadataV2Field { Name = "tmk", Type = MetadataV2FieldType.String, Nullable = true },
                new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = true, SemanticRoles = ["geometry.primary"] }
            ],
            Spatial = new MetadataV2ObjectSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Polygon,
                PrimaryGeometryField = "shape"
            }
        };
        var fields = ExportFields.Resolve(resource, "tmk");
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(2, [
                Feature("111"),
                Feature("111")
            ]));

        var read = () => ExportFeatureCapture.ReadAsync(reader, 7, fields, CancellationToken.None);

        await read.Should().ThrowAsync<ExportException>()
            .WithMessage("*Identity field 'tmk' must hold a unique, non-empty value for every feature.*");
    }

    private static Feature Feature(string tmk) => new()
    {
        Id = 1,
        Geometry = null,
        Attributes = new Dictionary<string, object?> { ["tmk"] = tmk }.ToImmutableDictionary()
    };
}
