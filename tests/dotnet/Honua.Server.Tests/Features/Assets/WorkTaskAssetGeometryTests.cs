// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Forms;
using Honua.Server.Features.Tasks;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Assets;

public sealed class WorkTaskAssetGeometryTests
{
    [UnitTest]
    public async Task CopyAsync_SignedAssetId_UsesPublishedSridAndCanonicalIdentity()
    {
        var target = await TargetAsync();
        var reader = Substitute.For<IFeatureReader>();
        var filters = Substitute.For<IFilterExpressionService>();
        filters.Translate(Arg.Any<FilterExpression>(), target.Resource!).Returns(call =>
            FilterTranslationResult.Success(call.Arg<FilterExpression>(), new SqlFragment("objectid = $1", [-7L])));
        var wkb = new WKBWriter().Write(new Point(1000, 2000));
        reader.QueryAsync(17, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>()).Returns(
            QueryResult<Feature>.Create(1, [Feature.Create(-7, wkb, ImmutableDictionary<string, object?>.Empty)]));
        var service = new WorkTaskAssetGeometry(reader, filters, Substitute.For<ILayerAccessAuthorizer>());
        var profile = new AssetProfile { ProfileId = Guid.NewGuid(), ServiceId = "service", LayerId = 0,
            Name = "Assets", Fields = new AssetProfileFieldMap { AssetId = "objectid" }, GeometryType = "Point",
            ConditionLabels = [], State = AssetProfileState.Active, Version = 1, CreatedBy = "admin", UpdatedBy = "admin",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };

        var result = await service.CopyAsync(target, (profile, "-007"), CancellationToken.None);

        result.Error.Should().BeNull();
        result.AssetId.Should().Be("-7");
        result.Srid.Should().Be(3857);
        result.Wkb.Should().Equal(wkb);
        await reader.Received(1).QueryAsync(17, Arg.Is<FeatureQuery>(query => query.OutputSrid == 3857 && query.Limit == 2),
            Arg.Any<CancellationToken>());
    }

    private static async Task<FormTargetMetadataResolution> TargetAsync()
    {
        var graph = new TestMetadataV2GraphBuilder().AddConnection("managed", "Managed")
            .AddResource("resource", "Assets", fields: [new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer }],
                spatial: new MetadataV2ResourceSpatial { GeometryType = MetadataV2GeometryType.Point,
                    PrimaryGeometryField = "shape", SpatialReference = new MetadataV2SpatialReference { Srid = 3857, Crs = "EPSG:3857" } })
            .AddStorageBinding("binding", "resource", "assets", "managed", storageLayerId: 17)
            .AddService("service", "service", protocols: ["FeatureServer"])
            .AddPublication("publication", "service", "resource", layerIndex: 0, storageBindingId: "binding",
                publicationType: MetadataV2PublicationType.EsriFeatureLayer).BuildProvider();
        return FormTargetMetadataResolver.Resolve(await graph.GetCurrentAsync(), new FormTargetDefinition { ServiceId = "service", LayerId = 0 });
    }
}
