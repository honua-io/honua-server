// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
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
        var path = Path.Join(Path.GetTempPath(), $"v2-graph-{Guid.NewGuid():N}.json");
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
        var path = Path.Join(Path.GetTempPath(), $"v2-graph-{Guid.NewGuid():N}.json");
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
        var path = Path.Join(Path.GetTempPath(), $"v2-graph-bad-{Guid.NewGuid():N}.json");
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
        var path = Path.Join(Path.GetTempPath(), $"v2-graph-nope-{Guid.NewGuid():N}.json");
        using var provider = new FileMetadataV2GraphProvider(path, NullLogger<FileMetadataV2GraphProvider>.Instance);

        Func<Task> act = async () => await provider.GetCurrentAsync();
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    private static async Task WriteGraphAsync(string path, MetadataV2Graph graph)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, graph, MetadataV2JsonContext.Default.MetadataV2Graph);
    }

    [UnitTheory]
    [InlineData("\"future-field-type\"")]
    [InlineData("987")]
    [InlineData("18446744073709551616")]
    [InlineData("-9223372036854775809")]
    [Operation(Operations.Metadata)]
    public async Task GetCurrentAsync_UnfamiliarSemanticFields_RetainsTokensThroughUnrelatedEdit(string token)
    {
        var graph = SampleGraph();
        var dataset = graph.Resources[0] with
        {
            Spatial = new MetadataV2ResourceSpatial { PrimaryGeometryField = "shape" },
            Temporal = new MetadataV2ResourceTemporal { StartTimeField = "start", EndTimeField = "end" },
            SchemaFields = [new() { Name = "shape" }, new() { Name = "start" }, new() { Name = "end" }]
        };
        var node = JsonSerializer.SerializeToNode(graph with { Resources = [dataset] },
            MetadataV2JsonContext.Default.MetadataV2Graph)!;
        foreach (var field in node["resources"]![0]!["schemaFields"]!.AsArray())
        {
            field!["type"] = JsonNode.Parse(token);
        }
        var path = Path.Join(Path.GetTempPath(), $"v2-graph-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, node.ToJsonString());
        try
        {
            using var provider = new FileMetadataV2GraphProvider(path, NullLogger<FileMetadataV2GraphProvider>.Instance);
            var loaded = await provider.GetCurrentAsync();
            var edited = loaded.Graph with
            {
                Resources = [loaded.Graph.Resources[0] with { Metadata = loaded.Graph.Resources[0].Metadata with { Title = "Edited title" } }]
            };
            MetadataV2GraphValidator.Validate(edited).Errors.Should().BeEmpty();
            await WriteGraphAsync(path, edited);
            using var coldProvider = new FileMetadataV2GraphProvider(path, NullLogger<FileMetadataV2GraphProvider>.Instance);
            var reloaded = await coldProvider.GetCurrentAsync();
            reloaded.Graph.Resources[0].Metadata.Title.Should().Be("Edited title");
            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            foreach (var field in persisted.RootElement.GetProperty("resources")[0].GetProperty("schemaFields").EnumerateArray())
            {
                field.GetProperty("type").GetRawText().Should().Be(token);
            }
        }
        finally
        {
            File.Delete(path);
        }
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
