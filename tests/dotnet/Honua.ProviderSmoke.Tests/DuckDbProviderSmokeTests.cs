// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.ProviderSmoke.Tests;

/// <summary>
/// Interface-level HTTP-stack smoke coverage for the DuckDB provider (honua-server#2947).
/// Boots a real ASP.NET Core host with <c>DataSource:Provider=duckdb</c> against a
/// standalone, file-backed DuckDB database. See <see cref="PrimaryProviderSmokeTestsBase"/>
/// for the shared assertions.
/// </summary>
[Trait("Provider", "DuckDb")]
public sealed class DuckDbProviderSmokeTests : PrimaryProviderSmokeTestsBase, IClassFixture<DuckDbProviderWebAppFixture>
{
    private readonly DuckDbProviderWebAppFixture _fixture;

    public DuckDbProviderSmokeTests(DuckDbProviderWebAppFixture fixture)
    {
        _fixture = fixture;
    }

    protected override HttpClient Client => _fixture.Client;

    // The bbox and raster-tile smoke cases live in PrimaryProviderSmokeTestsBase since
    // the honua-server#2965 EWKB converter fix landed; they run here against DuckDB and
    // against MySQL through the shared base.
}
