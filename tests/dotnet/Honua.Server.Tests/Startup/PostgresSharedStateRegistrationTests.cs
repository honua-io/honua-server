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

public sealed class PostgresSharedStateRegistrationTests
{
    [UnitTest]
    public void AddPostgresSharedState_WithoutSystemDataSource_RegistersNoLeaseStore()
    {
        using var provider = BuildProvider(dataSource: null);

        provider.GetService<ILeaseStore>().Should().BeNull();
    }

    [UnitTest]
    public void AddPostgresSharedState_WithSystemDataSource_RegistersPostgresLeaseStore()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=honua;Username=honua");

        using var provider = BuildProvider(dataSource);

        provider.GetService<ILeaseStore>().Should().BeOfType<PostgresLeaseStore>();
    }

    private static ServiceProvider BuildProvider(NpgsqlDataSource? dataSource)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (dataSource is not null)
        {
            services.AddSingleton(dataSource);
        }

        services.AddPostgresSharedState(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }
}
