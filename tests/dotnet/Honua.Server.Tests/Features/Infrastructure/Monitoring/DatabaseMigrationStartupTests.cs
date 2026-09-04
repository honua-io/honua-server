// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Regression coverage for startup database-migration handling.
///
/// The published server image runs in the Test environment, where Program skips the
/// infrastructure composition root so a fixture/harness can swap the data providers.
/// That composition root is the only site that registers a provider-specific
/// <see cref="IDatabaseMigrationRunner"/>, so a standalone container booted that way has a
/// connection string but no runner. Startup must skip migrations gracefully (matching how
/// the PostGIS preflight check treats its provider-specific checker) instead of crashing on
/// an unresolved GetRequiredService, which exited the container with code 139 and broke the
/// honua-console live-integration nightly.
/// </summary>
public sealed class DatabaseMigrationStartupTests
{
    [Fact]
    public async Task Startup_WithConnectionStringButNoMigrationRunner_SkipsMigrationsInsteadOfCrashing()
    {
        await using var baseFactory = new TestWebApplicationFactory();

        // Reproduce the published-container scenario on top of the standard test host: a
        // connection string is present (the base factory configures one) and migrations are
        // NOT skipped by configuration, but no provider registered an IDatabaseMigrationRunner.
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            // Added last, so this wins over the base HONUA_SKIP_MIGRATIONS=true: we want the
            // runner-resolution path to execute rather than short-circuit on the skip guard.
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["HONUA_SKIP_MIGRATIONS"] = "false"
                }));

            // Drop the base factory's stub runner so the active "provider" registers none,
            // exactly as the Test-environment server image does.
            builder.ConfigureTestServices(services => services.RemoveAll<IDatabaseMigrationRunner>());
        });

        // Building the host (via the first client) runs Program's startup migration routine.
        // Before the fix this threw InvalidOperationException because no IDatabaseMigrationRunner
        // was registered, aborting boot — CreateClient would have thrown here.
        using var client = factory.CreateClient();

        var migrationState = factory.Services.GetRequiredService<MigrationState>();
        migrationState.Status.Should().Be(MigrationLifecycleStatus.Skipped);
        migrationState.IsReady.Should().BeTrue();
        migrationState.StatusMessage.Should().Contain("migration runner");
    }
}
