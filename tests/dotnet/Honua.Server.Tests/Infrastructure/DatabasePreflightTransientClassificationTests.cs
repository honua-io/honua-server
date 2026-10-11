// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Sockets;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// The startup compatibility preflight must tell an unreachable database (no free connection slots,
/// a timeout, a socket failure) apart from an incompatible one. Run 38133126673 (aws-serverless,
/// redis-off) reported SQLSTATE 53300 as incompatible and every cold start exited.
/// </summary>
[Protocol(TestProtocols.Health)]
public sealed class DatabasePreflightTransientClassificationTests
{
    private const string SlotsExhaustedClass = "SQLSTATE 53300 (class 53: insufficient resources)";

    [UnitTheory]
    [Operation(Operations.HealthCheck)]
    [InlineData("53300")] // too_many_connections: remaining connection slots are reserved
    [InlineData("53400")] // configuration_limit_exceeded
    [InlineData("53000")] // insufficient_resources
    [InlineData("57P03")] // cannot_connect_now
    [InlineData("08006")] // connection_failure
    public void IsTransient_WithAvailabilitySqlState_ReturnsTrue(string sqlState)
    {
        PostgresPreflightFailureClassifier.IsTransient(BuildPostgresException(sqlState)).Should().BeTrue();
    }

    [UnitTheory]
    [Operation(Operations.HealthCheck)]
    [InlineData("0A000")] // feature_not_supported: PostGIS library/extension version mismatch
    [InlineData("42704")] // undefined_object: type geometry does not exist
    [InlineData("58P01")] // undefined_file: could not access the postgis-3 library
    [InlineData("28P01")] // invalid_password: misconfiguration, never an outage
    [InlineData("3D000")] // invalid_catalog_name: database does not exist
    public void IsTransient_WithIncompatibilityOrMisconfigurationSqlState_ReturnsFalse(string sqlState)
    {
        PostgresPreflightFailureClassifier.IsTransient(BuildPostgresException(sqlState)).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void IsTransient_WithNpgsqlTimeout_ReturnsTrue()
    {
        var timeout = new NpgsqlException(
            "Exception while connecting",
            new TimeoutException("Timeout during connection attempt"));

        PostgresPreflightFailureClassifier.IsTransient(timeout).Should().BeTrue();
        PostgresPreflightFailureClassifier.Describe(timeout).Should().Be("timeout");
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void IsTransient_WithNpgsqlStreamFailure_UsesNpgsqlTransientFlag()
    {
        var streamFailure = new NpgsqlException(
            "Exception while reading from stream",
            new IOException("Unable to read data from the transport connection"));

        streamFailure.IsTransient.Should().BeTrue();
        PostgresPreflightFailureClassifier.IsTransient(streamFailure).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void IsTransient_WithPermanentNpgsqlFailure_ReturnsFalse()
    {
        // TLS, certificate, and protocol failures are a base NpgsqlException with no SQLSTATE
        // and IsTransient == false. They must fail startup instead of degrading forever.
        var permanent = new NpgsqlException(
            "The remote certificate was rejected by the provided RemoteCertificateValidationCallback.");
        var wrapped = new InvalidOperationException("compatibility probe failed", permanent);

        permanent.IsTransient.Should().BeFalse();
        PostgresPreflightFailureClassifier.IsTransient(permanent).Should().BeFalse();
        PostgresPreflightFailureClassifier.IsTransient(wrapped).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void IsTransient_WithSqlStateInsideNpgsqlException_UsesTheServerCode()
    {
        var exhausted = new NpgsqlException("Failed to connect", BuildPostgresException("53300"));
        var rejected = new NpgsqlException("Failed to connect", BuildPostgresException("28P01"));

        PostgresPreflightFailureClassifier.IsTransient(exhausted).Should().BeTrue();
        PostgresPreflightFailureClassifier.IsTransient(rejected).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void Describe_WithSlotExhaustion_NamesSqlStateClassOnly()
    {
        var exhausted = new NpgsqlException(
            "Failed to connect to db.internal.example:5432 with Password=hunter2",
            BuildPostgresException("53300"));

        var description = PostgresPreflightFailureClassifier.Describe(exhausted);

        description.Should().Be(SlotsExhaustedClass);
        description.Should().NotContain("example").And.NotContain("hunter2");
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void Describe_WithSocketFailure_OmitsProviderMessage()
    {
        var refused = new NpgsqlException(
            "Failed to connect to 10.0.1.5:5432",
            new SocketException((int)SocketError.ConnectionRefused));

        PostgresPreflightFailureClassifier.IsTransient(refused).Should().BeTrue();
        PostgresPreflightFailureClassifier.Describe(refused).Should().Be("socket error (ConnectionRefused)");
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void Unavailable_IsNotCompatibleButIsTransient()
    {
        var result = DatabaseCompatibilityResult.Unavailable(SlotsExhaustedClass);

        result.IsCompatible.Should().BeFalse();
        result.IsTransientFailure.Should().BeTrue();
        result.FailureClass.Should().Be(SlotsExhaustedClass);
        result.ErrorMessage.Should().Contain("53300");
        DatabaseCompatibilityResult.Incompatible("PostGIS extension is not installed.")
            .IsTransientFailure.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckWithBoundedRetry_WhenStillTransient_StopsAfterTwoRetries()
    {
        var checker = Substitute.For<IDatabaseCompatibilityChecker>();
        checker.CheckCompatibilityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(DatabaseCompatibilityResult.Unavailable(SlotsExhaustedClass));
        var delays = new List<TimeSpan>();

        var result = await StartupDatabasePreflight.CheckWithBoundedRetryAsync(
            checker,
            "Host=unused",
            StartupDatabasePreflight.TransientRetryDelays,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        result.IsTransientFailure.Should().BeTrue();
        await checker.Received(3).CheckCompatibilityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        delays.Should().Equal(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1));
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckWithBoundedRetry_WhenTransientThenCompatible_ReturnsCompatible()
    {
        var checker = Substitute.For<IDatabaseCompatibilityChecker>();
        checker.CheckCompatibilityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                DatabaseCompatibilityResult.Unavailable(SlotsExhaustedClass),
                DatabaseCompatibilityResult.Compatible("PostgreSQL 15.17", "3.4.6", null, ["postgis"]));

        var result = await StartupDatabasePreflight.CheckWithBoundedRetryAsync(
            checker, "Host=unused", StartupDatabasePreflight.TransientRetryDelays, (_, _) => Task.CompletedTask, CancellationToken.None);

        result.IsCompatible.Should().BeTrue();
        await checker.Received(2).CheckCompatibilityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckWithBoundedRetry_WhenIncompatible_DoesNotRetry()
    {
        var checker = Substitute.For<IDatabaseCompatibilityChecker>();
        checker.CheckCompatibilityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(DatabaseCompatibilityResult.Incompatible("PostGIS extension is not installed."));

        var result = await StartupDatabasePreflight.CheckWithBoundedRetryAsync(
            checker, "Host=unused", StartupDatabasePreflight.TransientRetryDelays, (_, _) => Task.CompletedTask, CancellationToken.None);

        result.IsCompatible.Should().BeFalse();
        result.IsTransientFailure.Should().BeFalse();
        await checker.Received(1).CheckCompatibilityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [Operation(Operations.HealthCheck)]
    [InlineData("honua-api", null, null, true)]      // Lambda, operator silent: degrade
    [InlineData("honua-api", "false", null, false)]  // Lambda, explicit config: honoured
    [InlineData("honua-api", null, "false", false)]  // Lambda, explicit env override: honoured
    [InlineData(null, null, null, false)]            // long-lived host: fail fast (default)
    public void ApplyServerlessDefault_EnablesDegradedStartOnlyOnLambdaWhenUnset(
        string? lambdaFunctionName,
        string? configured,
        string? environmentOverride,
        bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [StartupDatabasePreflight.AwsLambdaFunctionNameKey] = lambdaFunctionName,
                [StartupDatabasePreflight.DegradedStartKey] = configured,
                [StartupDatabasePreflight.DegradedStartEnvironmentKey] = environmentOverride,
            })
            .Build();
        var bound = new StartupResilienceOptions
        {
            DegradedStartEnabled = bool.TryParse(configured ?? environmentOverride, out var value) && value,
        };

        StartupDatabasePreflight.ApplyServerlessDefault(bound, configuration)
            .DegradedStartEnabled.Should().Be(expected);
    }

    private static PostgresException BuildPostgresException(string sqlState)
        => new(
            messageText: "test",
            severity: "FATAL",
            invariantSeverity: "FATAL",
            sqlState: sqlState);
}
