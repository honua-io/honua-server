// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Server.Features.Infrastructure.Coordination;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Server.Tests.Startup;

public sealed class PostgresSharedStateRegistrationTests
{
    [UnitTest]
    public async Task AddPostgresSharedState_WithASystemDatabase_ResolvesThePostgresStores()
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=honua;Username=honua;Password=honua");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(dataSource);
        services.AddPostgresSharedState(new ConfigurationBuilder().Build());

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IVersionJobStore>().Should().BeOfType<PostgresVersionJobStore>();
        provider.GetRequiredService<ITileCacheGenerationCheckpointStore>().Should().BeOfType<PostgresTileCacheGenerationCheckpointStore>();
    }

    [UnitTest]
    public async Task AddPostgresSharedState_WithoutASystemDatabase_ResolvesProcessLocalStores()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgresSharedState(new ConfigurationBuilder().Build());

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IVersionJobStore>().Should().BeOfType<InMemoryVersionJobStore>();
        provider.GetRequiredService<ITileCacheGenerationCheckpointStore>().Should().BeOfType<InMemoryTileCacheGenerationCheckpointStore>();
    }
}
