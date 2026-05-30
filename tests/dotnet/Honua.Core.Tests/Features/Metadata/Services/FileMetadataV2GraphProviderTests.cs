// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Metadata.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Metadata.Services;

/// <summary>
/// Tests for the file-backed Metadata v2 graph provider.
/// </summary>
[Protocol(ProtocolNames.TestQuality)]
public sealed class FileMetadataV2GraphProviderTests
{
    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task LoadsAndCachesGraphSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"v2-graph-{Guid.NewGuid():N}.json");
        await WriteGraphAsync(path, SampleGraph());
        try
        {
            using var provider = new FileMetadataV2GraphProvider(path, NullLogger<FileMetadataV2GraphProvider>.Instance);

            var first = await provider.GetCurrentAsync();
            var second = await provider.GetCurrentAsync();

            first.Should().BeSameAs(second);
            first.Etag.Should().StartWith("\"");
            first.Graph.Revision.Should().Be(7);
            first.Index.ResourcesById.Should().ContainKey("resource.parcels");
            first.Index.ServicesByName.Should().ContainKey("Features");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetByRevision_ReturnsCurrentWhenRevisionMatches()
    {
        var path = Path.Combine(Path.GetTempPath(), $"v2-graph-{Guid.NewGuid():N}.json");
        await WriteGraphAsync(path, SampleGraph());
        try
        {
            using var provider = new FileMetadataV2GraphProvider(path, NullLogger<FileMetadataV2GraphProvider>.Instance);

            var match = await provider.GetByRevisionAsync(7);
            var missing = await provider.GetByRevisionAsync(99);

            match.Should().NotBeNull();
            missing.Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task InvalidGraph_ThrowsOnLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"v2-graph-bad-{Guid.NewGuid():N}.json");
        var invalid = SampleGraph() with
        {
            Publications =
            [
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub.dangling", Name = "dangling" },
                    ResourceId = "resource.does-not-exist",
                    ServiceId = "service.features",
                }
            ],
        };
        await WriteGraphAsync(path, invalid);
        try
        {
            using var provider = new FileMetadataV2GraphProvider(path, NullLogger<FileMetadataV2GraphProvider>.Instance);

            Func<Task> act = async () => await provider.GetCurrentAsync();
            await act.Should().ThrowAsync<InvalidDataException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task MissingFile_ThrowsFileNotFound()
    {
        var path = Path.Combine(Path.GetTempPath(), $"v2-graph-nope-{Guid.NewGuid():N}.json");
        using var provider = new FileMetadataV2GraphProvider(path, NullLogger<FileMetadataV2GraphProvider>.Instance);

        Func<Task> act = async () => await provider.GetCurrentAsync();
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    private static async Task WriteGraphAsync(string path, MetadataV2Graph graph)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, graph, MetadataV2JsonContext.Default.MetadataV2Graph);
    }

    private static MetadataV2Graph SampleGraph()
    {
        return new MetadataV2Graph
        {
            Revision = 7,
            Environment = "test",
            GeneratedAt = DateTimeOffset.Parse("2026-05-20T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource.parcels", Name = "parcels" },
                    Type = MetadataV2ResourceType.FeatureDataset,
                    StorageBindingIds = ["storage.parcels.postgis"],

                }
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "storage.parcels.postgis", Name = "parcels-postgis" },
                    ResourceId = "resource.parcels",
                    StorageType = MetadataV2StorageType.RelationalTable,
                    Locator = "public.parcels",
                    StorageLayerId = 0,
                }
            ],
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service.features", Name = "Features" },
                    Protocols = [ServiceProtocols.OgcFeatures],
                    Route = "/ogc/features",
                }
            ],
            Publications =
            [
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub.parcels.features", Name = "parcels" },
                    ResourceId = "resource.parcels",
                    ServiceId = "service.features",
                    StorageBindingId = "storage.parcels.postgis",
                    PublicationType = MetadataV2PublicationType.OgcCollection,
                }
            ],
        };
    }
}
