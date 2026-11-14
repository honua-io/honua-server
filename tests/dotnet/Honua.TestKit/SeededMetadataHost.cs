// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Mixins;
using Xunit;

namespace Honua.TestKit;

// One host for a test class whose cases change only the host's metadata graph. Each case calls
// RestoreSeededStateAsync first: any queued graph swap is dropped and the seeded graph returns
// under a new revision, so etags and revision-keyed reads never see an earlier case's writes, and
// cached service and layer metadata is evicted, so tag-keyed output caches never serve an earlier
// case's response.
public abstract class SeededMetadataHost : IAsyncLifetime
{
    private MetadataV2Graph? _seeded;

    protected SeededMetadataHost(WebAppFixture application) => Application = application;

    public WebAppFixture Application { get; }

    public async Task InitializeAsync()
    {
        await Application.InitializeAsync();
        _seeded = Application.GetCurrentV2GraphSnapshot().Graph;
    }

    public Task RestoreSeededStateAsync()
    {
        var seeded = _seeded ?? throw new InvalidOperationException("The host must be initialized first.");
        var revision = Application.GetCurrentV2GraphSnapshot().Graph.Revision + 1;
        var provider = WebAppFixtureMetadataV2GraphMutationMixin.RequireProvider(Application);
        provider.ClearPendingActivation();
        provider.SetGraph(seeded with { Revision = revision }, schema: Application.MetadataGraphSchema);
        return Application.GetService<OutputCacheInvalidationService>()
            .InvalidateServiceCatalogAsync(serviceId: null, layerIds: null, CancellationToken.None);
    }

    public Task DisposeAsync() => Application.DisposeAsync();
}
