// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

public sealed class GeometryServiceDatumProfiles : IAsyncLifetime, IAsyncDisposable
{
    private readonly DatumGridPostgresFixture _withGrid = new();
    private readonly DatumGridPostgresFixture _withoutGrid = new();
    private Task? _withGridInitialization;
    private Task? _withoutGridInitialization;

    public Task InitializeAsync() => Task.CompletedTask;

    // xUnit runs the cases in this class fixture's test collection serially.
    public async Task<DatumGridPostgresFixture> GetWithGridAsync()
    {
        await (_withGridInitialization ??= _withGrid.InitializeAsync(includeNadconGrid: true));
        return _withGrid;
    }

    public async Task<DatumGridPostgresFixture> GetWithoutGridAsync()
    {
        await (_withoutGridInitialization ??= _withoutGrid.InitializeAsync(includeNadconGrid: false));
        return _withoutGrid;
    }

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    public async Task DisposeAsync()
    {
        try
        {
            await _withGrid.DisposeAsync();
        }
        finally
        {
            await _withoutGrid.DisposeAsync();
        }
    }
}
