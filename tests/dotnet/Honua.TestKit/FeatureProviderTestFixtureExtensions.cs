// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Infrastructure;

namespace Honua.TestKit;

public static class FeatureProviderTestFixtureExtensions
{
    public static WebAppFixture DecorateFeatureReaders(
        this WebAppFixture fixture, Func<IFeatureReader, IFeatureReader> decorate)
        => fixture.DecorateService(decorate)
            .DecorateService<IFeatureDataProvider>(inner => new ReaderDecoratingProvider(inner, decorate));

    public static WebAppFixture UseInMemoryFeatureProvider(this WebAppFixture fixture, TestFeatureStore store)
        => fixture.ReplaceService<IFeatureDataProvider>(new InMemoryFeatureProvider(store));

    public static void RemoveRasterResources(this WebAppFixture fixture)
    {
        var graph = fixture.GetCurrentV2GraphSnapshot().Graph;
        var rasterIds = graph.Resources
            .Where(resource => resource.Type == MetadataV2ResourceType.RasterDataset)
            .Select(resource => resource.Metadata.Id)
            .ToHashSet(StringComparer.Ordinal);
        var provider = (TestMetadataV2GraphProvider)fixture.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(graph with
        {
            Resources = graph.Resources.Where(resource => !rasterIds.Contains(resource.Metadata.Id)).ToArray(),
            StorageBindings = graph.StorageBindings.Where(binding => !rasterIds.Contains(binding.ResourceId)).ToArray(),
            Publications = graph.Publications.Where(publication => !rasterIds.Contains(publication.ResourceId)).ToArray(),
            Revision = graph.Revision + 1
        }, schema: fixture.CurrentSchema);
    }

    private sealed class ReaderDecoratingProvider(
        IFeatureDataProvider inner, Func<IFeatureReader, IFeatureReader> decorate)
        : IFeatureDataProvider, IBindableFeatureDataProvider
    {
        public string ProviderName => inner.ProviderName;
        public FeatureProviderCapabilities Capabilities => inner.Capabilities;
        public IFeatureReader Reader => decorate(inner.Reader);
        public IFeatureWriter? Writer => inner.Writer;

        public IFeatureReader CreateReaderForBinding(FeatureProviderBinding binding)
            => decorate(inner is IBindableFeatureDataProvider bindable
                ? bindable.CreateReaderForBinding(binding)
                : inner.Reader);
    }

    private sealed class InMemoryFeatureProvider(TestFeatureStore store) : IFeatureDataProvider
    {
        public string ProviderName => DataProviderNames.Postgis;
        public FeatureProviderCapabilities Capabilities { get; } = new()
        {
            Edits = FeatureProviderEditCapabilities.ReadWrite
        };
        public IFeatureReader Reader => store;
        public IFeatureWriter Writer => store;
    }
}
