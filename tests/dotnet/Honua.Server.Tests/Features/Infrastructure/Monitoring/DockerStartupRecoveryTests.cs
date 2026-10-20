// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

public sealed class DockerStartupRecoveryTests
{
    [Theory]
    [Trait("Tier", "Integration")]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public async Task DevelopmentHost_RecoversTransientStartupAndKeepsWritesFencedUntilMigrationsFinish(
        bool preflightFails, bool throws, bool containerDefault)
    {
        var databaseAvailable = false;
        var compatible = DatabaseCompatibilityResult.Compatible("PostgreSQL 18", "3.6", null, ["postgis"]);
        var checker = Substitute.For<IDatabaseCompatibilityChecker>();
        checker.CheckCompatibilityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
            !preflightFails || Volatile.Read(ref databaseAvailable)
                ? Task.FromResult(compatible)
                : throws
                    ? Task.FromException<DatabaseCompatibilityResult>(StartingUp())
                    : Task.FromResult(DatabaseCompatibilityResult.Unavailable("SQLSTATE 57P03 (class 57)", StartingUp())));
        var runner = new RecoveringRunner(!preflightFails, throws);
        await using var baseFactory = TestWebApplicationFactory.CreateForEnvironment("Development");
        await using var factory = Configure(baseFactory, checker, runner, containerDefault);
        using var client = factory.CreateClient();
        var context = factory.Services.GetRequiredService<DegradedStartupContext>();

        context.IsAwaitingDatabase.Should().BeTrue();
        using (var ready = await client.GetAsync(new Uri("/healthz/ready", UriKind.Relative)))
        {
            ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            ready.Headers.GetValues(ReadinessReasonCodes.HeaderName)
                .Should().ContainSingle().Which.Should().Be(ReadinessReasonCodes.DatabaseStartupDegraded);
        }
        using (var live = await client.GetAsync(new Uri("/healthz/live", UriKind.Relative)))
            live.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var write = await client.PostAsync(new Uri("/rest/services/parcels/FeatureServer/0/applyEdits", UriKind.Relative), null))
            write.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        runner.Completed.Should().Be(0);
        if (preflightFails) runner.Calls.Should().Be(0, "preflight defers migrations to the sole background recovery owner");

        Volatile.Write(ref databaseAvailable, true);
        runner.AllowCompletion.TrySetResult();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!context.IsRecovered && DateTime.UtcNow < deadline) await Task.Delay(25);
        context.IsRecovered.Should().BeTrue();
        context.IsAwaitingDatabase.Should().BeFalse();
        runner.Completed.Should().Be(1);
        runner.Calls.Should().Be(preflightFails ? 1 : 2);
        factory.Services.GetRequiredService<MigrationState>().Status.Should().Be(MigrationLifecycleStatus.Succeeded);
        using var recovered = await client.GetAsync(new Uri("/healthz/ready", UriKind.Relative));
        recovered.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [Trait("Tier", "Integration")]
    [InlineData(true)]
    [InlineData(false)]
    public void DevelopmentContainer_PermanentDatabaseOrMigrationFailureStillRefusesStartup(bool incompatible)
    {
        var checker = Substitute.For<IDatabaseCompatibilityChecker>();
        checker.CheckCompatibilityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            incompatible
                ? DatabaseCompatibilityResult.Incompatible("PostGIS is not installed.", "PostgreSQL 18", ["plpgsql"])
                : DatabaseCompatibilityResult.Compatible("PostgreSQL 18", "3.6", null, ["postgis"]));
        var runner = Substitute.For<IDatabaseMigrationRunner>();
        runner.RunMigrationsAsync(Arg.Any<string>(), Arg.Any<Assembly>(), Arg.Any<CancellationToken>())
            .Returns(DatabaseMigrationResult.Failed(new InvalidOperationException("Permanent migration failure.")));
        using var baseFactory = TestWebApplicationFactory.CreateForEnvironment("Development");
        using var factory = Configure(baseFactory, checker, runner, containerDefault: true);

        var start = () => factory.CreateClient();
        start.Should().Throw<InvalidOperationException>();
    }

    private static WebApplicationFactory<Program> Configure(TestWebApplicationFactory baseFactory,
        IDatabaseCompatibilityChecker checker, IDatabaseMigrationRunner runner, bool containerDefault)
        => baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DOTNET_RUNNING_IN_CONTAINER", containerDefault ? "true" : "false");
            if (!containerDefault) builder.UseSetting("HONUA_DB_DEGRADED_START", "true");
            builder.UseSetting("HONUA_SKIP_MIGRATIONS", "false");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["HONUA_SKIP_MIGRATIONS"] = "false" }));
            builder.UseSetting("Database:StartupResilience:RetryBaseDelay", "00:00:00.050");
            builder.UseSetting("Database:StartupResilience:RetryMaxDelay", "00:00:00.200");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDatabaseCompatibilityChecker>();
                services.AddSingleton(checker);
                services.RemoveAll<IDatabaseMigrationRunner>();
                services.AddSingleton(runner);
            });
        });

    [Fact]
    [Trait("Tier", "Integration")]
    public void DevelopmentContainer_ExplicitRecoveryDisabledFailsFastOnTransientPreflight()
    {
        var checker = Substitute.For<IDatabaseCompatibilityChecker>();
        checker.CheckCompatibilityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(DatabaseCompatibilityResult.Unavailable("SQLSTATE 57P03 (class 57)", StartingUp()));
        using var baseFactory = TestWebApplicationFactory.CreateForEnvironment("Development");
        using var configured = Configure(baseFactory, checker, Substitute.For<IDatabaseMigrationRunner>(), containerDefault: true);
        using var factory = configured.WithWebHostBuilder(builder => builder.UseSetting("HONUA_DB_DEGRADED_START", "false"));

        var start = () => factory.CreateClient();
        start.Should().Throw<InvalidOperationException>().WithMessage("PostGIS preflight check failed:*57P03*");
    }

    private static PostgresException StartingUp() => new("The database system is starting up.", "FATAL", "FATAL", "57P03");

    private sealed class RecoveringRunner(bool failFirst, bool throws) : IDatabaseMigrationRunner
    {
        private int _calls;
        private int _completed;
        public int Calls => Volatile.Read(ref _calls);
        public int Completed => Volatile.Read(ref _completed);
        public TaskCompletionSource AllowCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DatabaseMigrationPlan> PlanMigrationsAsync(string connectionString, Assembly migrationsAssembly,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task<DatabaseMigrationResult> RunMigrationsAsync(string connectionString, Assembly migrationsAssembly,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1 && failFirst)
            {
                if (throws) throw StartingUp();
                return DatabaseMigrationResult.Failed(StartingUp());
            }
            await AllowCompletion.Task.WaitAsync(cancellationToken);
            Interlocked.Increment(ref _completed);
            return DatabaseMigrationResult.Succeeded();
        }
    }
}
