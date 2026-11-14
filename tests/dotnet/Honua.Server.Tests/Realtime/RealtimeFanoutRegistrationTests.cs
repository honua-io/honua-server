// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Threading.Channels;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Core.Features.Realtime;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Server.Features.Realtime;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Server.Tests.Realtime;

public sealed partial class RealtimeFanoutProviderTests
{
    [UnitTheory]
    [InlineData(null, null, false)]
    [InlineData(null, "MultiNode", true)]
    [InlineData("auto", "multinode", true)]
    [InlineData("auto", "SingleNode", false)]
    [InlineData("in-process", "MultiNode", false)]
    [InlineData("postgres", null, true)]
    public void ProviderChoice_FollowsRealtimeProviderAndDeploymentMode(string? provider, string? mode, bool postgres)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Realtime:Provider"] = provider, ["Deployment:Mode"] = mode })
            .Build();

        RealtimeServiceCollectionExtensions.UsesPostgresProvider(configuration).Should().Be(postgres);
    }

    [UnitTest]
    public void ProviderChoice_UnknownProvider_IsRefused()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Realtime:Provider"] = "redis" })
            .Build();

        var choose = () => RealtimeServiceCollectionExtensions.UsesPostgresProvider(configuration);

        choose.Should().Throw<InvalidOperationException>().WithMessage("*auto, in-process or postgres*");
    }

    [UnitTest]
    public async Task Registration_MultiNode_ResolvesTheCrossNodeProviderAndTheTail()
    {
        await using var provider = BuildServices(new() { ["Deployment:Mode"] = "MultiNode" }, hostsDatabasePollingLoops: true);

        provider.GetRequiredService<IRealtimeFanoutProvider>().Capabilities.CrossNode.Should().BeTrue();
        provider.GetServices<IHostedService>().OfType<RealtimeTailBackgroundService>().Should().ContainSingle();
    }

    [Theory]
    [InlineData("SingleNode")]
    [InlineData("MultiNode")]
    [Trait("Tier", "Fast")]
    public async Task Registration_WithoutDataSource_HostedServicesStartWithoutDatabaseDependencies(string mode)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Deployment:Mode"] = mode }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddRealtimeFanout(configuration, hostsDatabasePollingLoops: true);
        await using var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>().ToArray();
        hosted.OfType<RealtimeTailBackgroundService>().Should().ContainSingle();
        foreach (var service in hosted)
        {
            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);
        }
    }

    [UnitTest]
    public async Task Registration_WithoutDatabasePollingLoops_RegistersNoTail()
    {
        await using var provider = BuildServices(new() { ["Deployment:Mode"] = "MultiNode" }, hostsDatabasePollingLoops: false);

        provider.GetRequiredService<IRealtimeFanoutProvider>().Capabilities.CrossNode.Should().BeTrue();
        provider.GetServices<IHostedService>().OfType<RealtimeTailBackgroundService>().Should().BeEmpty();
    }

    [UnitTest]
    public void Listener_ProxyHost_RunsPollOnly()
    {
        var listener = CreateListener(null, "Host=honua.proxy-abc.example.test;Database=honua;Username=u");

        listener.PollOnlyReason.Should().Contain("proxy");
        listener.IsConnected.Should().BeFalse();
    }

    [UnitTest]
    public void Listener_WithoutItsOwnConnectionString_UsesTheResolvedSystemConnectionString()
    {
        var listener = CreateListener(null, "Host=db.example.test;Database=honua;Username=u;Password=resolved");

        listener.PollOnlyReason.Should().BeNull();
    }

    [UnitTest]
    public void Listener_UnparseableConnectionString_RunsPollOnlyInsteadOfFailingStartup()
    {
        var listener = CreateListener("arn:aws:secretsmanager:region:account:secret:name", "Host=db.example.test;Database=honua");

        listener.PollOnlyReason.Should().Contain("could not be parsed");
    }

    [UnitTest]
    public async Task Registration_ListenerUsesTheDataSourceConnectionStringNotTheRawSetting()
    {
        // The raw setting is a secret reference; only the data source holds the resolved string.
        await using var provider = BuildServices(
            new() { ["Deployment:Mode"] = "MultiNode", ["ConnectionStrings:DefaultConnection"] = "secret-reference:honua/db" },
            hostsDatabasePollingLoops: true,
            rawConnectionString: "secret-reference:honua/db");

        provider.GetRequiredService<RealtimeListener>().PollOnlyReason.Should().BeNull();
    }

    [IntegrationTest]
    public async Task Registration_ResolvedPasswordFromProductionDataSource_ListenerAuthenticates()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Deployment:Mode"] = "MultiNode",
            ["ConnectionStrings:DefaultConnection"] = "secret-reference:honua/db"
        }).Build();
        await using var dataSource = PostgresDataSourceFactory.Create(
            database.ConnectionString, schemaHeadersEnabled: false, new ConnectionLimits());
        new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Password.Should().NotBeNullOrEmpty();
        var services = new ServiceCollection().AddLogging().AddSingleton(dataSource);
        services.AddRealtimeFanout(configuration, hostsDatabasePollingLoops: true);
        await using var provider = services.BuildServiceProvider();
        var listener = provider.GetRequiredService<RealtimeListener>();
        using var stopping = new CancellationTokenSource();
        var running = listener.RunAsync(() => { }, stopping.Token);
        try
        {
            await WaitForAsync(() => listener.IsConnected, "the production data source preserves resolved authentication for LISTEN");
        }
        finally
        {
            await stopping.CancelAsync();
            await running;
        }
    }

    private static RealtimeListener CreateListener(string? listenerConnectionString, string? systemConnectionString)
        => RealtimeListener.Create(
            string.IsNullOrWhiteSpace(listenerConnectionString) ? systemConnectionString : listenerConnectionString,
            Microsoft.Extensions.Options.Options.Create(new RealtimeOptions()),
            NullLogger<RealtimeListener>.Instance);

    private static ServiceProvider BuildServices(
        Dictionary<string, string?> settings,
        bool hostsDatabasePollingLoops,
        string rawConnectionString = "Host=localhost;Database=honua;Username=u")
    {
        settings["ConnectionStrings:DefaultConnection"] = rawConnectionString;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddSingleton(NpgsqlDataSource.Create("Host=localhost;Database=honua;Username=u"));
        services.AddSingleton(provider => new JobStoreDatabase(provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddRealtimeFanout(configuration, hostsDatabasePollingLoops);
        return services.BuildServiceProvider();
    }

    // Another task: its own dispatcher, which nobody here runs; its appends notify every task.
    private static PostgresRealtimeFanoutProvider CreateProvider(RealtimeLogHarness harness)
        => new(harness.Log, harness.CreateDispatcher(), new RealtimePresenceBatcher(harness.Log));

    private static async Task AppendAsync(RealtimeLogHarness harness, string family, string scope, int count)
        => await harness.Log.AppendAsync(
            family,
            Enumerable.Range(0, count).Select(n => new RealtimeLogRow(scope, "test", RealtimeLogHarness.Payload(new { n }))).ToArray(),
            null,
            CancellationToken.None);

    private static async Task WaitUntilLiveAsync(RealtimeSubscription subscription)
        => await WaitForAsync(() => subscription.IsLive, "the subscription goes live");

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            deadline.Elapsed.Should().BeLessThan(_deliveryTimeout, $"{what} in time");
            await Task.Delay(20);
        }
    }

    private static async Task<RealtimeDelivery> ReadOneAsync(ChannelReader<RealtimeDelivery> reader)
    {
        using var timeout = new CancellationTokenSource(_deliveryTimeout);
        return await reader.ReadAsync(timeout.Token);
    }

    private static async Task<IReadOnlyList<RealtimeEvent>> ReadEventsAsync(ChannelReader<RealtimeDelivery> reader, int count)
    {
        using var timeout = new CancellationTokenSource(_deliveryTimeout);
        var events = new List<RealtimeEvent>();
        while (events.Count < count)
        {
            var delivery = await reader.ReadAsync(timeout.Token);
            delivery.Should().BeOfType<RealtimeDelivery.Event>();
            events.Add(((RealtimeDelivery.Event)delivery).Value);
        }

        return events;
    }
}
