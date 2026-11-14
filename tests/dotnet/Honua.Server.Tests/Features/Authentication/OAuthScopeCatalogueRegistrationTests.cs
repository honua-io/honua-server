// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Infrastructure.Authentication;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Authentication;

[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.Security)]
public sealed class OAuthScopeCatalogueRegistrationTests
{
    [UnitTest]
    public void Registration_WithoutDatabase_PreservesLocalFallback()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddPostgresSharedState(configuration);
        services.AddPostgresSharedState(configuration);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOAuthScopeCatalogue>().Should().BeOfType<InMemoryOAuthScopeCatalogue>();
        services.Count(item => item.ServiceType == typeof(IOAuthScopeCatalogue)).Should().Be(1);
    }

    [UnitTest]
    public void Registration_WithDatabase_SelectsSharedStore()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=registration_only");
        services.AddSingleton(dataSource);
        services.AddSingleton(new JobStoreDatabase(dataSource));
        services.AddSingleton<IOAuthScopeCatalogue, InMemoryOAuthScopeCatalogue>();
        services.AddPostgresSharedState(configuration);
        services.AddPostgresSharedState(configuration);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOAuthScopeCatalogue>().Should().BeOfType<PostgresOAuthScopeCatalogue>();
        services.Count(item => item.ServiceType == typeof(IOAuthScopeCatalogue)).Should().Be(1);
    }

    [UnitTest]
    public void Registration_WithCustomStore_PreservesOverride()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        var custom = Substitute.For<IOAuthScopeCatalogue>();
        services.AddSingleton(custom);
        services.AddPostgresSharedState(configuration);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOAuthScopeCatalogue>().Should().BeSameAs(custom);
    }
}
