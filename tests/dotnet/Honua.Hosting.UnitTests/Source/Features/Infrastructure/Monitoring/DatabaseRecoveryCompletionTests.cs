// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

public sealed class DatabaseRecoveryCompletionTests
{
    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recovery_MissingDeferredMigrationOrSchemaService_NeverDeclaresReady(bool migrations)
    {
        var context = new DegradedStartupContext();
        context.MarkDegraded(migrations, Assembly.GetExecutingAssembly(), schemaGuardPending: !migrations);
        var state = new MigrationState();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var recovery = Service(context, state, services, new StartupResilienceOptions());

        await recovery.StartAsync(CancellationToken.None);
        await WaitForAsync(() => context.RecoveryFailure is not null);
        context.IsAwaitingDatabase.Should().BeTrue();
        context.IsRecovered.Should().BeFalse();
        state.IsReady.Should().BeFalse();
        await recovery.StopAsync(CancellationToken.None);
    }

    [UnitTest]
    public async Task Recovery_BackgroundMigrationsDisabled_WaitsForVerifiedExternalSchemaWithoutRunningMigrations()
    {
        var context = new DegradedStartupContext();
        context.MarkDegraded(true, Assembly.GetExecutingAssembly());
        var state = new MigrationState();
        var schemaReady = false;
        var guard = Substitute.For<IDatabaseSchemaGuard>();
        guard.VerifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
            Volatile.Read(ref schemaReady) ? Task.CompletedTask : Task.FromException(new DatabaseSchemaFloorException(
                "001_Initial.sql", DatabaseSchemaFloorFailureKind.MigrationNotApplied, "Migration is pending.")));
        var runner = Substitute.For<IDatabaseMigrationRunner>();
        using var services = new ServiceCollection().AddSingleton(guard).AddSingleton(runner).BuildServiceProvider();
        using var recovery = Service(context, state, services, new StartupResilienceOptions
        {
            RetryMigrationsInBackground = false,
            RetryBaseDelay = TimeSpan.FromMilliseconds(10),
            RetryMaxDelay = TimeSpan.FromMilliseconds(25)
        });

        await recovery.StartAsync(CancellationToken.None);
        await WaitForAsync(() => context.ReasonCode == ReadinessReasonCodes.SchemaFloor);
        context.IsAwaitingDatabase.Should().BeTrue();
        state.IsReady.Should().BeFalse();
        Volatile.Write(ref schemaReady, true);
        await WaitForAsync(() => context.IsRecovered);
        state.Status.Should().Be(MigrationLifecycleStatus.Skipped);
        await runner.DidNotReceive().RunMigrationsAsync(Arg.Any<string>(), Arg.Any<Assembly>(), Arg.Any<CancellationToken>());
        await recovery.StopAsync(CancellationToken.None);
    }

    private static DatabaseRecoveryBackgroundService Service(DegradedStartupContext context, MigrationState state,
        IServiceProvider services, StartupResilienceOptions options)
        => new(context, state, new DatabaseCompatibilityState(), new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = "Host=unused;Database=unused"
                }).Build(),
            options, services, NullLogger<DatabaseRecoveryBackgroundService>.Instance);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        condition().Should().BeTrue("the bounded recovery observation must complete");
    }
}
