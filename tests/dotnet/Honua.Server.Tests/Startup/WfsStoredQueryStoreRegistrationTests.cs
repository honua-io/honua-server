// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.Server.Features.Protocols.Wfs20;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Honua.Server.Tests.Startup;

public sealed class WfsStoredQueryStoreRegistrationTests
{
    [UnitTest]
    public void WithoutSystemDatabase_ValidatesAndResolvesInMemoryStore()
    {
        var services = new ServiceCollection();
        services.AddWfsStoredQueryStore();

        using var provider = Validate(services);

        provider.GetRequiredService<IWfsStoredQueryStore>().Should().BeOfType<InMemoryWfsStoredQueryStore>();
    }

    [UnitTest]
    public void WithSystemDatabase_ValidatesAndResolvesSharedStore()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused");
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddSingleton(provider => new JobStoreDatabase(provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddWfsStoredQueryStore();

        using var provider = Validate(services);

        var store = provider.GetRequiredService<IWfsStoredQueryStore>();
        store.Should().BeOfType<PostgresWfsStoredQueryStore>();
        provider.GetRequiredService<IWfsStoredQueryStore>().Should().BeSameAs(store);
    }

    [UnitTest]
    public void SystemDatabaseRemovedAfterRegistration_ValidatesAndResolvesInMemoryStore()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused");
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddSingleton(provider => new JobStoreDatabase(provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddWfsStoredQueryStore();
        services.RemoveAll<NpgsqlDataSource>();

        using var provider = Validate(services);

        provider.GetRequiredService<IWfsStoredQueryStore>().Should().BeOfType<InMemoryWfsStoredQueryStore>();
    }

    private static ServiceProvider Validate(IServiceCollection services)
        => services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
}
