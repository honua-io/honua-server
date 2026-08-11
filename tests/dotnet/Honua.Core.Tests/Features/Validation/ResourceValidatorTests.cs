// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Core.Tests.Features.Validation;

/// <summary>
/// Tests for <see cref="ResourceValidator"/> integer-collection-id resolution, in
/// particular the case where one storage layer index is published more than once
/// (a feature dataset and a raster sidecar that share the same layer id, as the
/// client-compat seed does for layer 2000). The integer-collection-id contract must
/// resolve to the storage-backed feature resource so callers such as OGC API Maps can
/// resolve an integer storage-layer handle instead of 404-ing on the raster sidecar.
/// </summary>
[Protocol(ProtocolNames.TestQuality)]
public sealed class ResourceValidatorTests
{
    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task ValidateLayerV2Async_WhenLayerIndexSharedWithRasterSidecar_ResolvesStorageBackedResource()
    {
        var provider = new TestMetadataV2GraphProvider(SharedLayerIndexGraph());
        var validator = new ResourceValidator(provider);

        var result = await validator.ValidateLayerV2Async(2000);

        result.IsValid.Should().BeTrue();
        result.Resource.Should().NotBeNull();
        // Must NOT resolve to the raster sidecar (res-image-layer-2000), whose primary
        // storage binding has no integer StorageLayerId.
        result.Resource!.Metadata.Id.Should().Be("res-layer-2000");

        var snapshot = await provider.GetCurrentAsync();
        snapshot.ResolveStorageLayerId(result.Resource).Should().Be(2000);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task ValidateCollectionV2Async_WithIntegerId_ResolvesStorageBackedResource()
    {
        var provider = new TestMetadataV2GraphProvider(SharedLayerIndexGraph());
        var validator = new ResourceValidator(provider);

        var result = await validator.ValidateCollectionV2Async("2000");

        result.IsValid.Should().BeTrue();
        result.Resource!.Metadata.Id.Should().Be("res-layer-2000");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task ValidateServiceLayerV2Async_WithProtocol_PrefersCanonicalPublicationType()
    {
        var provider = new TestMetadataV2GraphProvider(MixedProtocolPublicationGraph());
        var validator = new ResourceValidator(provider);

        var result = await validator.ValidateServiceLayerV2Async(
            "mixed-service",
            7,
            ServiceProtocols.FeatureServer);

        result.IsValid.Should().BeTrue();
        result.Resource.Resource.Metadata.Id.Should().Be("res-feature");
        result.Resource.Publication.PublicationType.Should().Be(MetadataV2PublicationType.EsriFeatureLayer);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task ValidateServiceLayerV2Async_MapServer_UsesFeaturePublicationFallback()
    {
        var graph = MixedProtocolPublicationGraph();
        graph = graph with
        {
            Services =
            [
                graph.Services[0] with
                {
                    Protocols = [ServiceProtocols.FeatureServer, ServiceProtocols.MapServer],
                },
            ],
        };
        var validator = new ResourceValidator(new TestMetadataV2GraphProvider(graph));

        var result = await validator.ValidateServiceLayerV2Async(
            "mixed-service",
            7,
            ServiceProtocols.MapServer);

        result.IsValid.Should().BeTrue();
        result.Resource.Resource.Metadata.Id.Should().Be("res-feature");
        result.Resource.Publication.PublicationType.Should().Be(MetadataV2PublicationType.EsriFeatureLayer);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task ValidateServiceLayerV2Async_WithSharedName_ResolvesProtocolSpecificService()
    {
        var graph = MixedProtocolPublicationGraph();
        graph = graph with
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-aggregate", Name = "shared" },
                    Protocols = [ServiceProtocols.OgcFeatures],
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-feature", Name = "shared" },
                    Protocols = [ServiceProtocols.FeatureServer],
                },
            ],
            Publications =
            [
                graph.Publications[0] with { ServiceId = "service-aggregate" },
                graph.Publications[1] with { ServiceId = "service-feature" },
            ],
        };
        var validator = new ResourceValidator(new TestMetadataV2GraphProvider(graph));

        var result = await validator.ValidateServiceLayerV2Async(
            "shared",
            7,
            ServiceProtocols.FeatureServer);

        result.IsValid.Should().BeTrue();
        result.Resource.Service.Metadata.Id.Should().Be("service-feature");
        result.Resource.Resource.Metadata.Id.Should().Be("res-feature");
        result.Resource.Publication.PublicationType.Should().Be(MetadataV2PublicationType.EsriFeatureLayer);
    }

    private static MetadataV2Graph MixedProtocolPublicationGraph()
    {
        return new MetadataV2Graph
        {
            Revision = 1,
            Environment = "Production",
            GeneratedAt = DateTimeOffset.Parse(
                "2026-06-01T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture),
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "res-ogc", Name = "OGC resource" },
                    Type = MetadataV2ResourceType.FeatureDataset,
                },
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "res-feature", Name = "FeatureServer resource" },
                    Type = MetadataV2ResourceType.FeatureDataset,
                },
            ],
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "mixed-service", Name = "mixed-service" },
                    Protocols = [ServiceProtocols.OgcFeatures, ServiceProtocols.FeatureServer],
                },
            ],
            Publications =
            [
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub-ogc", Name = "7" },
                    ResourceId = "res-ogc",
                    ServiceId = "mixed-service",
                    PublicationType = MetadataV2PublicationType.OgcCollection,
                    Identifier = new MetadataV2PublicationIdentifier { Value = "7", IsNumeric = true },
                },
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub-feature", Name = "7" },
                    ResourceId = "res-feature",
                    ServiceId = "mixed-service",
                    PublicationType = MetadataV2PublicationType.EsriFeatureLayer,
                    Identifier = new MetadataV2PublicationIdentifier { Value = "7", IsNumeric = true },
                },
            ],
        };
    }

    /// <summary>
    /// Builds a graph where storage layer index 2000 is published twice: a raster
    /// sidecar publication (ordered first, no integer StorageLayerId) and a
    /// storage-backed feature publication. Mirrors the client-compat seed shape.
    /// </summary>
    private static MetadataV2Graph SharedLayerIndexGraph()
    {
        return new MetadataV2Graph
        {
            Revision = 1,
            Environment = "Production",
            GeneratedAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "res-image-layer-2000", Name = "Browser Points imagery" },
                    Type = MetadataV2ResourceType.RasterDataset,
                    StorageBindingIds = ["storage-image-layer-2000"],
                    PrimaryStorageBindingId = "storage-image-layer-2000",
                },
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "res-layer-2000", Name = "Browser Points" },
                    Type = MetadataV2ResourceType.FeatureDataset,
                    StorageBindingIds = ["storage-layer-2000"],
                    PrimaryStorageBindingId = "storage-layer-2000",
                },
            ],
            StorageBindings =
            [
                // Raster sidecar: no integer storage-layer handle.
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "storage-image-layer-2000", Name = "storage-image-layer-2000" },
                    ResourceId = "res-image-layer-2000",
                    StorageType = MetadataV2StorageType.RelationalTable,
                    Locator = "honua.raster_data",
                    StorageLayerId = null,
                },
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "storage-layer-2000", Name = "storage-layer-2000" },
                    ResourceId = "res-layer-2000",
                    StorageType = MetadataV2StorageType.RelationalTable,
                    Locator = "public.features",
                    StorageLayerId = 2000,
                },
            ],
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "svc-browser-compat-image", Name = "browser_compat" },
                    Protocols = [ServiceProtocols.ImageServer],
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "svc-browser-compat-map", Name = "browser_compat" },
                    Protocols = [ServiceProtocols.OgcApiMaps],
                },
            ],
            Publications =
            [
                // Raster sidecar publication ordered first — the previous
                // FirstOrDefault(LayerIndex == 2000) match returned this resource,
                // which has no integer StorageLayerId and caused OGC API Maps 404s.
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub-browser-compat-image-2000", Name = "2000" },
                    ResourceId = "res-image-layer-2000",
                    ServiceId = "svc-browser-compat-image",
                    StorageBindingId = "storage-image-layer-2000",
                    PublicationType = MetadataV2PublicationType.EsriImageLayer,
                    Identifier = new MetadataV2PublicationIdentifier { Value = "2000", IsNumeric = true },
                },
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub-browser-compat-map-2000", Name = "2000" },
                    ResourceId = "res-layer-2000",
                    ServiceId = "svc-browser-compat-map",
                    StorageBindingId = "storage-layer-2000",
                    PublicationType = MetadataV2PublicationType.EsriMapLayer,
                    Identifier = new MetadataV2PublicationIdentifier { Value = "2000", IsNumeric = true },
                },
            ],
        };
    }
}
