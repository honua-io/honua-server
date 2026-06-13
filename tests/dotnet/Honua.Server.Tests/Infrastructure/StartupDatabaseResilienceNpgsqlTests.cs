// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Sockets;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Provider-aware tests for the degraded-start classifier (#1632): verifies real Npgsql exception
/// types are correctly bucketed as transient (connectivity) vs. fatal (misconfiguration), and that
/// the degraded-start context arms recovery without crashing the process.
/// </summary>
[Protocol(TestProtocols.Health)]
public sealed class StartupDatabaseResilienceNpgsqlTests
{
    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void IsTransientConnectivityError_WithNpgsqlConnectionFailure_ReturnsTrue()
    {
        // A connection-refused style failure surfaces as an NpgsqlException wrapping a socket error.
        var exception = new NpgsqlException(
            "Failed to connect to 127.0.0.1:5432",
            new SocketException((int)SocketError.ConnectionRefused));

        StartupDatabaseResilience.IsTransientConnectivityError(exception).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void IsTransientConnectivityError_WithTooManyConnections_ReturnsTrue()
    {
        // SQLSTATE 53300 (too_many_connections) is the exact pool/slot-exhaustion failure from #1632.
        var exception = BuildPostgresException("53300");
        StartupDatabaseResilience.IsTransientConnectivityError(exception).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void IsTransientConnectivityError_WithCannotConnectNow_ReturnsTrue()
    {
        // SQLSTATE 57P03 (cannot_connect_now) — server still starting up.
        var exception = BuildPostgresException("57P03");
        StartupDatabaseResilience.IsTransientConnectivityError(exception).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void IsTransientConnectivityError_WithSyntaxError_ReturnsFalse()
    {
        // SQLSTATE 42601 (syntax_error) — a bad migration script must still fail loudly.
        var exception = BuildPostgresException("42601");
        StartupDatabaseResilience.IsTransientConnectivityError(exception).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void DegradedStartupContext_StartsNotDegraded()
    {
        var context = new DegradedStartupContext();
        context.IsDegraded.Should().BeFalse();
        context.MigrationsPending.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void DegradedStartupContext_MarkDegraded_RecordsRecoveryInputs()
    {
        var context = new DegradedStartupContext();
        var assembly = typeof(StartupDatabaseResilienceNpgsqlTests).Assembly;

        context.MarkDegraded(migrationsPending: true, migrationsAssembly: assembly);

        context.IsDegraded.Should().BeTrue();
        context.MigrationsPending.Should().BeTrue();
        context.MigrationsAssembly.Should().BeSameAs(assembly);
    }

    private static PostgresException BuildPostgresException(string sqlState)
        => new(
            messageText: "test",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: sqlState);
}
