// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wcs20;

public abstract class SubstitutedRasterStoreApplication : IAsyncLifetime, IAsyncDisposable
{
    protected SubstitutedRasterStoreApplication()
    {
        Application = new WebAppFixture().ReplaceService(RasterStore);
    }

    public IRasterStore RasterStore { get; } = Substitute.For<IRasterStore>();

    public List<RasterQuery> ExportQueries { get; } = [];

    public WebAppFixture Application { get; }

    public void RestoreDefaults()
    {
        RasterStore.ClearSubstitute(ClearOptions.All);
        ExportQueries.Clear();
        ConfigureDefaults(RasterStore, ExportQueries);
    }

    protected abstract void ConfigureDefaults(IRasterStore rasterStore, List<RasterQuery> exportQueries);

    public Task InitializeAsync() => Application.InitializeAsync();

    public Task DisposeAsync() => Application.DisposeAsync();

    async ValueTask IAsyncDisposable.DisposeAsync() => await DisposeAsync();
}
