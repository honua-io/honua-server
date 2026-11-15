// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.EmbedGovernance;
using Honua.Core.Features.EmbedGovernance.Abstractions;
using Honua.Server.Features.Admin.EmbedGovernance;
using Honua.Server.Features.Console;
using Honua.Server.Features.Console.Services;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Server.Tests.Startup;

public sealed class ConsoleStateStoreRegistrationTests
{
    [UnitTest]
    public async Task AddConsoleStateStores_WithASystemDatabase_KeepsConsoleStateInPostgres()
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=honua;Username=honua;Password=honua");
        await using var provider = BuildProvider(dataSource);

        provider.GetRequiredService<IConsoleContentStore>().Should().BeOfType<PostgresConsoleContentStore>();
        provider.GetRequiredService<IConsoleShareStore>().Should().BeOfType<PostgresConsoleShareStore>();
        provider.GetRequiredService<IConsoleOpenDataStore>().Should().BeOfType<PostgresConsoleOpenDataStore>();
        provider.GetRequiredService<IEmbedKeyStore>().Should().BeOfType<PostgresEmbedKeyStore>();
        provider.GetRequiredService<ConsolePersistenceNotice>().Should().BeEquivalentTo(ConsolePersistenceNotice.Postgres);
    }

    [UnitTest]
    public async Task AddConsoleStateStores_WithoutASystemDatabase_KeepsConsoleStateInTheTask()
    {
        await using var provider = BuildProvider(dataSource: null);

        provider.GetRequiredService<IConsoleContentStore>().Should().BeOfType<InMemoryConsoleContentStore>();
        provider.GetRequiredService<IConsoleShareStore>().Should().BeOfType<InMemoryConsoleShareStore>();
        provider.GetRequiredService<IConsoleOpenDataStore>().Should().BeOfType<InMemoryConsoleOpenDataStore>();
        provider.GetRequiredService<IEmbedKeyStore>().Should().BeOfType<InMemoryEmbedKeyStore>();
        provider.GetRequiredService<ConsolePersistenceNotice>().Durable.Should().BeFalse();
    }

    [UnitTest]
    public async Task StartupGuard_ProcessLocalStateUnderATaskCeilingAboveOne_RefusesToStart()
    {
        var guard = Guard(ConsolePersistenceNotice.InMemory, ("Deployment:MaxTasks", "4"));

        var start = () => guard.StartAsync(CancellationToken.None);

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Configured replica count is 4*");
    }

    [UnitTest]
    public async Task StartupGuard_SharedStateUnderATaskCeilingAboveOne_Starts()
    {
        await Guard(ConsolePersistenceNotice.Postgres, ("Deployment:MaxTasks", "4")).StartAsync(CancellationToken.None);
        await Guard(ConsolePersistenceNotice.InMemory, ("Deployment:MaxTasks", "1")).StartAsync(CancellationToken.None);
    }

    private static ConsoleInMemoryStoreStartupGuard Guard(ConsolePersistenceNotice notice, params (string Key, string Value)[] settings)
        => new(
            NullLogger<ConsoleInMemoryStoreStartupGuard>.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
                .Build(),
            notice);

    private static ServiceProvider BuildProvider(NpgsqlDataSource? dataSource)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        if (dataSource is not null)
        {
            services.AddSingleton(dataSource);
        }

        services.AddConsoleStateStores(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }
}
