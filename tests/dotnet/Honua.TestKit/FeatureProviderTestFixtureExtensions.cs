// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.TestKit.Infrastructure;

namespace Honua.TestKit;

public static class FeatureProviderTestFixtureExtensions
{
    public static WebAppFixture DecorateFeatureReaders(
        this WebAppFixture fixture, Func<IFeatureReader, IFeatureReader> decorate)
        => fixture.DecorateService(decorate)
            .DecorateEachService<IFeatureDataProvider>(inner => new ReaderDecoratingProvider(inner, decorate));

    public static WebAppFixture UseInMemoryFeatureProvider(this WebAppFixture fixture, TestFeatureStore store)
        => fixture.ReplaceService<IFeatureDataProvider>(new InMemoryPostgisProvider(store));

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

    private sealed class InMemoryPostgisProvider(TestFeatureStore store) : IFeatureDataProvider
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
