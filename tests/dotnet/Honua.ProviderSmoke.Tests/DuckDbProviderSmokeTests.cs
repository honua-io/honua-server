// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
}
