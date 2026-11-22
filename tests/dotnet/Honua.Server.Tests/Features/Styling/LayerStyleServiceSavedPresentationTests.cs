// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Server.Features.Styling;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Styling;

// Every renderer and style document prefers a layer's saved presentation (the Console drawingInfo
// override) to its stored style, so a newer style write (OGC Styles PUT, admin style, SLD import)
// retires the override; otherwise the style just written would never show.
[Trait("Component", "Styling")]
public sealed class LayerStyleServiceSavedPresentationTests
{
    private const int StorageLayerId = 8377;
    private const string SavedKey = "geoservices:drawingInfo";

    [UnitTest]
    public async Task UpdateStyle_LayerWithSavedPresentation_RetiresTheOverride()
    {
        var store = new RecordingGraphStore(Snapshot(SavedPresentationExtensions()));
        var resource = (await store.GetCurrentAsync()).Graph.Resources.Single();

        var result = await Service(store).UpdateStyleAsync(resource, StorageLayerId, mapLibreStyle: null, drawingInfo: NewDrawingInfo());

        result.Status.Should().Be(LayerStyleUpdateStatus.Updated);
        store.SaveCount.Should().Be(1);
        store.LastSaved!.Resources.Single().Extensions.Should().NotContainKey(SavedKey);
        store.LastSaved.Resources.Single().Extensions.Should().ContainKey("other");
    }

    [UnitTest]
    public async Task UpdateStyle_LayerWithoutSavedPresentation_LeavesTheGraphAlone()
    {
        var store = new RecordingGraphStore(Snapshot(OtherExtensions()));
        var resource = (await store.GetCurrentAsync()).Graph.Resources.Single();

        var result = await Service(store).UpdateStyleAsync(resource, StorageLayerId, mapLibreStyle: null, drawingInfo: NewDrawingInfo());

        result.Status.Should().Be(LayerStyleUpdateStatus.Updated);
        store.SaveCount.Should().Be(0);
    }

    private static LayerStyleService Service(IMetadataV2GraphStore store)
    {
        var catalog = Substitute.For<ILayerStyleCatalog>();
        catalog.SetStyleAsync(default, default!, default!, default, default, default)
            .ReturnsForAnyArgs(new LayerStyleDefinition { LayerId = StorageLayerId, StyleVersion = 1 });
        return new LayerStyleService(catalog, NullLogger<LayerStyleService>.Instance, graphStore: store);
    }

    private static JsonElement NewDrawingInfo()
        => JsonElement.Parse("""{"renderer":{"type":"simple","symbol":{"type":"esriSMS","color":[20,160,60,255],"size":8}}}""");

    private static Dictionary<string, JsonElement> OtherExtensions()
        => new() { ["other"] = JsonElement.Parse("""{"keep":true}""") };

    private static Dictionary<string, JsonElement> SavedPresentationExtensions()
    {
        var extensions = OtherExtensions();
        extensions[SavedKey] = JsonElement.Parse("""{"renderer":{"type":"simple","symbol":{"type":"esriSMS","color":[240,170,0,255],"size":10}}}""");
        return extensions;
    }

    private static MetadataV2GraphSnapshot Snapshot(Dictionary<string, JsonElement> extensions)
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource(
                "resource-bng",
                "bng_points",
                spatial: new MetadataV2ObjectSpatial { GeometryType = MetadataV2GeometryType.Point },
                extensions: extensions)
            .AddStorageBinding("binding-bng", "resource-bng", "public.bng_points", storageLayerId: StorageLayerId)
            .AddService("service-bng", "bng")
            .AddPublication("publication-bng", "service-bng", "resource-bng", storageBindingId: "binding-bng")
            .Build();
        return new MetadataV2GraphSnapshot(graph, "etag-1", DateTimeOffset.UnixEpoch);
    }

    private sealed class RecordingGraphStore(MetadataV2GraphSnapshot initial) : IMetadataV2GraphStore
    {
        private MetadataV2GraphSnapshot _current = initial;

        public int SaveCount { get; private set; }

        public MetadataV2Graph? LastSaved { get; private set; }

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_current);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(null);

        public Task<MetadataV2GraphSnapshot> SaveAsync(MetadataV2Graph graph, string? expectedEtag, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            LastSaved = graph;
            _current = new MetadataV2GraphSnapshot(graph, "etag-2", DateTimeOffset.UnixEpoch);
            return Task.FromResult(_current);
        }

        public Task<MetadataV2GraphSnapshot> ActivateRevisionAsync(long revision, string? expectedCurrentEtag, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
