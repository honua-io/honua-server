// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Server.Features.Collaboration.FeatureLocks;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Collaboration.FeatureLocks;

[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.Security)]
public sealed class FeatureLockStoreRegistrationTests
{
    [UnitTest]
    public void Registration_WithoutDatabase_PreservesLocalFallback()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddPostgresSharedState(configuration);
        services.AddFeatureLockCollaboration();
        services.AddPostgresSharedState(configuration);
        services.AddFeatureLockCollaboration();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IFeatureLockService>().Should().BeOfType<InMemoryFeatureLockService>();
        services.Count(item => item.ServiceType == typeof(IFeatureLockService)).Should().Be(1);
    }

    [UnitTest]
    public void Registration_WithDatabase_SelectsSharedStore()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=registration_only");
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton(dataSource);
        services.AddSingleton(new JobStoreDatabase(dataSource));
        services.AddFeatureLockCollaboration();
        services.AddPostgresSharedState(configuration);
        services.AddFeatureLockCollaboration();
        services.AddPostgresSharedState(configuration);
        services.AddFeatureLockCollaboration();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IFeatureLockService>().Should().BeOfType<PostgresFeatureLockService>();
        provider.GetServices<IExpiredRowSweep>().OfType<PostgresFeatureLockService>().Should().ContainSingle()
            .Which.Should().BeSameAs(provider.GetRequiredService<IFeatureLockService>());
        services.Count(item => item.ServiceType == typeof(IFeatureLockService)).Should().Be(1);
    }

    [UnitTest]
    public void Registration_WithCustomStore_PreservesOverride()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        var custom = Substitute.For<IFeatureLockService>();
        services.AddSingleton(custom);
        services.AddPostgresSharedState(configuration);
        services.AddFeatureLockCollaboration();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IFeatureLockService>().Should().BeSameAs(custom);
    }
}
