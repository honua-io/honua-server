// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.Protocols.Ogc.Classic;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Protocols.OgcClassic.UnitTests;

public sealed class CapabilitiesDocumentCacheHelperTests
{
    [UnitTest]
    public async Task GetOrBuildAsync_LayerOmittedDuringBuild_DoesNotCacheDocument()
    {
        using var services = new ServiceCollection().AddSingleton(new MetadataDocumentByteCache()).BuildServiceProvider();
        var builds = 0;

        Task<PooledUtf8ContentResult> BuildWithOmittedLayerAsync() => BuildAsync(async () =>
        {
            builds++;
            await CapabilitiesLayerBuilder.TryBuildAsync(
                () => Task.FromException<string>(new IOException("transient")), "flaky-layer", NullLogger.Instance);
        });

        await CapabilitiesDocumentCacheHelper.GetOrBuildAsync(NewContext(services), "wms-key", BuildWithOmittedLayerAsync);
        await CapabilitiesDocumentCacheHelper.GetOrBuildAsync(NewContext(services), "wms-key", BuildWithOmittedLayerAsync);

        Assert.Equal(2, builds);
    }

    [UnitTest]
    public async Task GetOrBuildAsync_CompleteBuild_ServesCachedDocumentOnNextRequest()
    {
        using var services = new ServiceCollection().AddSingleton(new MetadataDocumentByteCache()).BuildServiceProvider();
        var builds = 0;

        Task<PooledUtf8ContentResult> BuildCompleteAsync() => BuildAsync(() =>
        {
            builds++;
            return Task.CompletedTask;
        });

        await CapabilitiesDocumentCacheHelper.GetOrBuildAsync(NewContext(services), "wms-key", BuildCompleteAsync);
        await CapabilitiesDocumentCacheHelper.GetOrBuildAsync(NewContext(services), "wms-key", BuildCompleteAsync);

        Assert.Equal(1, builds);
    }

    [UnitTest]
    public async Task GetOrBuildAsync_QueryTokenPresent_BypassesCache()
    {
        using var services = new ServiceCollection().AddSingleton(new MetadataDocumentByteCache()).BuildServiceProvider();
        var builds = 0;

        Task<PooledUtf8ContentResult> BuildCompleteAsync() => BuildAsync(() =>
        {
            builds++;
            return Task.CompletedTask;
        });

        var context = NewContext(services);
        context.Request.QueryString = new QueryString("?token=secret");
        await CapabilitiesDocumentCacheHelper.GetOrBuildAsync(context, "wms-key", BuildCompleteAsync);
        await CapabilitiesDocumentCacheHelper.GetOrBuildAsync(context, "wms-key", BuildCompleteAsync);

        Assert.Equal(2, builds);
    }

    private static async Task<PooledUtf8ContentResult> BuildAsync(Func<Task> buildLayers)
    {
        await buildLayers();
        var sb = new StringBuilder("<Capabilities/>");
        return PooledTextBuilder.ToUtf8Result(sb, "application/xml");
    }

    private static DefaultHttpContext NewContext(IServiceProvider services) => new() { RequestServices = services };
}
