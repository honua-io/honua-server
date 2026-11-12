// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Server.Features.Infrastructure.Coordination;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Server.Tests.Startup;

// Services that take an optional lease store resolve at host start, so a host without a system
// database must see no lease store rather than one that fails for want of a data source.
public sealed class PostgresSharedStateRegistrationTests
{
    [UnitTest]
    public void LeaseStore_WithoutSystemDatabase_IsAbsent()
    {
        using var provider = BuildProvider(services => { });

        provider.GetService<ILeaseStore>().Should().BeNull();
    }

    [UnitTest]
    public void LeaseStore_WithSystemDatabase_IsThePostgresLeaseRow()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=honua");
        using var provider = BuildProvider(services => services.AddSingleton(dataSource));

        provider.GetService<ILeaseStore>().Should().BeOfType<PostgresLeaseStore>();
    }

    private static ServiceProvider BuildProvider(Action<IServiceCollection> composeDatabase)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        composeDatabase(services);
        services.AddPostgresSharedState(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }
}
