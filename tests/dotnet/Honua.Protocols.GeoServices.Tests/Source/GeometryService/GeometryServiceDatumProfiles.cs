// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

public sealed class GeometryServiceDatumProfiles : IAsyncLifetime, IAsyncDisposable
{
    private DatumGridPostgresFixture? _withGrid;
    private DatumGridPostgresFixture? _withoutGrid;

    public Task InitializeAsync() => Task.CompletedTask;

    // xUnit runs the cases in this class fixture's test collection serially.
    public async Task<DatumGridPostgresFixture> GetWithGridAsync()
        => _withGrid ??= await CreateProfileAsync(static fixture => fixture.InitializeAsync(includeNadconGrid: true));

    public async Task<DatumGridPostgresFixture> GetWithoutGridAsync()
        => _withoutGrid ??= await CreateProfileAsync(static fixture => fixture.InitializeAsync(includeNadconGrid: false));

    private static async Task<DatumGridPostgresFixture> CreateProfileAsync(Func<DatumGridPostgresFixture, Task> initialize)
    {
        var fixture = new DatumGridPostgresFixture();
        try
        {
            await initialize(fixture);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    public async Task DisposeAsync()
    {
        try
        {
            if (_withGrid is not null)
            {
                await _withGrid.DisposeAsync();
            }
        }
        finally
        {
            if (_withoutGrid is not null)
            {
                await _withoutGrid.DisposeAsync();
            }
        }
    }
}
