// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
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
    public async Task CopyAsync_SignedAssetId_UsesPublicationBoundReaderAndCanonicalIdentity()
    {
        var (snapshot, target) = await TargetAsync();
        var boundReader = Substitute.For<IFeatureReader>();
        var (router, bindable) = CreateRouter(boundReader);
        var filters = Substitute.For<IFilterExpressionService>();
        filters.Translate(Arg.Any<FilterExpression>(), target.Resource!).Returns(call =>
            FilterTranslationResult.Success(call.Arg<FilterExpression>(), new SqlFragment("objectid = $1", [-7L])));
        var wkb = new WKBWriter().Write(new Point(1000, 2000));
        boundReader.QueryAsync(17, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>()).Returns(
            QueryResult<Feature>.Create(1, [Feature.Create(-7, wkb, ImmutableDictionary<string, object?>.Empty)]));
        var service = new WorkTaskAssetGeometry(router, filters, Substitute.For<ILayerAccessAuthorizer>());

        var result = await service.CopyAsync((snapshot, target), (CreateProfile(), "-007"), CancellationToken.None);

        result.Error.Should().BeNull();
        result.AssetId.Should().Be("-7");
        result.Srid.Should().Be(3857);
        result.Wkb.Should().Equal(wkb);
        bindable.Received(1).CreateReaderForBinding(
            Arg.Is<FeatureProviderBinding>(binding => binding.StorageLayerId == 17 && binding.Publication.Metadata.Id == "publication"));
        await boundReader.Received(1).QueryAsync(17, Arg.Is<FeatureQuery>(query => query.OutputSrid == 3857 && query.Limit == 2),
            Arg.Any<CancellationToken>());
    }

    private static (FeatureProviderQueryRouter Router, IBindableFeatureDataProvider Bindable) CreateRouter(IFeatureReader boundReader)
    {
        var provider = Substitute.For<IFeatureDataProvider, IBindableFeatureDataProvider>();
        provider.Capabilities.Returns(new FeatureProviderCapabilities { SupportsQuery = true });
        var bindable = (IBindableFeatureDataProvider)provider;
        bindable.CreateReaderForBinding(Arg.Is<FeatureProviderBinding>(binding =>
                binding.StorageLayerId == 17 && binding.Publication.Metadata.Id == "publication"))
            .Returns(boundReader);
        var providerRegistry = Substitute.For<IFeatureDataProviderRegistry>();
        providerRegistry.TryGetProvider("postgis", out Arg.Any<IFeatureDataProvider?>()).Returns(call =>
        {
            call[1] = provider;
            return true;
        });
        var connections = Substitute.For<ISecureConnectionRegistry>();
        connections.GetConnectionAsync("managed", Arg.Any<CancellationToken>())
            .Returns(new DataConnection { Id = "managed", Name = "Managed", Provider = DataProviderNames.Postgis });
        return (new FeatureProviderQueryRouter(connections, providerRegistry), bindable);
    }

    private static AssetProfile CreateProfile() => new()
    {
        ProfileId = Guid.NewGuid(),
        ServiceId = "service",
        LayerId = 0,
        Name = "Assets",
        Fields = new AssetProfileFieldMap { AssetId = "objectid" },
        GeometryType = "Point",
        ConditionLabels = [],
        State = AssetProfileState.Active,
        Version = 1,
        CreatedBy = "admin",
        UpdatedBy = "admin",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<(MetadataV2GraphSnapshot Snapshot, FormTargetMetadataResolution Target)> TargetAsync()
    {
        var graph = new TestMetadataV2GraphBuilder().AddConnection("managed", "Managed")
            .AddResource("resource", "Assets", fields: [new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer },
                    new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry }],
                spatial: new MetadataV2ObjectSpatial
                {
                    GeometryType = MetadataV2GeometryType.Point,
                    PrimaryGeometryField = "shape",
                    SpatialReference = new MetadataV2SpatialReference { Srid = 3857, Crs = "EPSG:3857" },
                })
            .AddStorageBinding("binding", "resource", "assets", "managed", storageLayerId: 17)
            .AddService("service", "service", protocols: ["FeatureServer"])
            .AddPublication("publication", "service", "resource", layerIndex: 0, storageBindingId: "binding",
                publicationType: MetadataV2LayerType.EsriFeatureLayer).BuildProvider();
        var snapshot = await graph.GetCurrentAsync();
        var target = FormTargetMetadataResolver.Resolve(snapshot, new FormTargetDefinition { ServiceId = "service", LayerId = 0 });
        return (snapshot, target);
    }
}
