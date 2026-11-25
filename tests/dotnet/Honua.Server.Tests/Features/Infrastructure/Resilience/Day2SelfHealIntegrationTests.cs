// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Sockets;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Resilience;

[Protocol(TestProtocols.Health)]
public sealed class Day2SelfHealIntegrationTests
{
    private const string SecretSentinel = "SYNTHETIC_SECRET_VALUE_987654";

    [UnitTheory]
    [Operation(Operations.HealthCheck)]
    [InlineData("57P01")]
    [InlineData("57P02")]
    [InlineData("57P03")]
    [InlineData("08001")]
    [InlineData("08006")]
    [InlineData("53300")]
    [InlineData("53400")]
    public void IsTransientConnectivityError_TransientPostgresSqlStates_ReturnsTrue(string sqlState)
    {
        var exception = BuildPostgresException(sqlState);

        StartupDatabaseResilience.IsTransientConnectivityError(exception).Should().BeTrue();
        StartupDatabaseResilience.IsTransientSqlState(sqlState).Should().BeTrue();
    }

    [UnitTheory]
    [Operation(Operations.HealthCheck)]
    [InlineData("28P01")]
    [InlineData("3D000")]
    [InlineData("42601")]
    [InlineData("42P01")]
    [InlineData("22P02")]
    public void IsTransientConnectivityError_NonTransientConfigOrSchemaSqlStates_ReturnsFalse(string sqlState)
    {
        var exception = BuildPostgresException(sqlState);

        StartupDatabaseResilience.IsTransientConnectivityError(exception).Should().BeFalse();
        StartupDatabaseResilience.IsTransientSqlState(sqlState).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void IsTransientConnectivityError_TimeoutAndSocketErrors_ReturnsTrue()
    {
        var timeout = new TimeoutException("connect timed out");
        var socket = new NpgsqlException("refused", new SocketException((int)SocketError.ConnectionRefused));

        StartupDatabaseResilience.IsTransientConnectivityError(timeout).Should().BeTrue();
        StartupDatabaseResilience.IsTransientConnectivityError(socket).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void IsTransientConnectivityError_AggregateWithPermanentSibling_ReturnsFalse()
    {
        var mixed = new AggregateException(
            BuildPostgresException("57P03"),
            new InvalidOperationException("bad connection string"));
        var allTransient = new AggregateException(
            BuildPostgresException("57P03"),
            new TimeoutException("timed out"));

        StartupDatabaseResilience.IsTransientConnectivityError(mixed).Should().BeFalse();
        StartupDatabaseResilience.IsTransientConnectivityError(allTransient).Should().BeTrue();
        StartupDatabaseResilience.IsTransientConnectivityError(null).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void GetRetryDelay_ExponentialBackoff_ClampsAtMaxAndRespectsFullJitter()
    {
        var baseDelay = TimeSpan.FromMilliseconds(250);
        var maxDelay = TimeSpan.FromSeconds(2);

        StartupDatabaseResilience.GetRetryDelay(0, baseDelay, maxDelay).Should().Be(baseDelay);
        StartupDatabaseResilience.GetRetryDelay(1, baseDelay, maxDelay).Should().Be(baseDelay);
        StartupDatabaseResilience.GetRetryDelay(2, baseDelay, maxDelay).Should().Be(TimeSpan.FromMilliseconds(500));
        StartupDatabaseResilience.GetRetryDelay(100, baseDelay, maxDelay).Should().Be(maxDelay);

        var jittered = StartupDatabaseResilience.GetRetryDelay(3, baseDelay, maxDelay, new Random(42));
        jittered.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        jittered.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(1));
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void DescribeFailureClass_PostgresException_SanitizesProviderMessage()
    {
        var exception = BuildPostgresException(
            "57P03",
            $"Host=db.internal;Password={SecretSentinel}");

        var described = StartupDatabaseResilience.DescribeFailureClass(exception);

        described.Should().Be("SQLSTATE 57P03 (class 57: operator intervention)");
        described.Should().NotContain(SecretSentinel);
        described.Should().NotContain("db.internal");
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task FileBackedLicenseService_CommunityWithAbsentPath_StartsWithoutCrash()
    {
        var missingPath = Path.Join(Path.GetTempPath(), $"honua-missing-{Guid.NewGuid():N}.json");
        var options = new LicenseOptions
        {
            Edition = HonuaEdition.Community,
            LicensePath = missingPath,
        };
        using var service = CreateLicenseService(options);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var status = service.GetCurrentStatus();
            status.Edition.Should().Be(HonuaEdition.Community);
            status.IsValid.Should().BeTrue();
            status.ValidationState.Should().Be(LicenseValidationState.NoLicenseConfigured);
            service.IsBlocked.Should().BeFalse();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task FileBackedLicenseService_ProWithAbsentPath_RefusesWithBoundedDiagnostics()
    {
        var missingPath = Path.Join(Path.GetTempPath(), $"honua-absent-{SecretSentinel}.json");
        var options = new LicenseOptions
        {
            Edition = HonuaEdition.Pro,
            LicensePath = missingPath,
        };
        using var service = CreateLicenseService(options);

        var act = () => service.StartAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("license missing");
        thrown.Which.Message.Should().NotContain(SecretSentinel);
        service.GetSnapshot().ValidationState.Should().Be(LicenseValidationState.MissingFile);
        service.IsBlocked.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task FileBackedLicenseService_MalformedFile_DoesNotLeakFileSecretContent()
    {
        var directory = Directory.CreateTempSubdirectory("honua-day2-license-");
        try
        {
            var licensePath = Path.Join(directory.FullName, "corrupt.json");
            await File.WriteAllTextAsync(licensePath, $"{{\"raw\":\"{SecretSentinel}\"}}");
            var logger = CreateMockLogger<FileBackedLicenseService>();
            var options = new LicenseOptions { Edition = HonuaEdition.Pro, LicensePath = licensePath };
            using var service = CreateLicenseService(options, logger);

            var act = () => service.StartAsync(CancellationToken.None);

            var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
            thrown.Which.Message.Should().Contain("license invalid");
            thrown.Which.Message.Should().NotContain(SecretSentinel);
            FindLoggedEvent(logger, eventId: 10002).Should().NotBeNull();
            AssertNoLogContains(logger, SecretSentinel);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task FileBackedLicenseService_SecretResolverFailure_LogsOnlyExceptionType()
    {
        var resolver = Substitute.For<ILicenseContentSecretResolver>();
        resolver.CanResolve(Arg.Any<string?>()).Returns(true);
        resolver.ResolveLicenseContentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(new InvalidOperationException($"Denied: {SecretSentinel}")));
        var logger = CreateMockLogger<FileBackedLicenseService>();
        var options = new LicenseOptions { LicenseContentSecretRef = "aws:secretsmanager:honua/license" };
        using var service = CreateLicenseService(options, logger, [resolver]);

        var act = () => service.StartAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().NotContain(SecretSentinel);
        FindLoggedEvent(logger, eventId: 10013).Should().NotBeNull();
        AssertNoLogContains(logger, SecretSentinel);
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task AuthenticationFailureLimiterStartupCheck_ForwardedHeadersDisabled_LogsWarning()
    {
        var logger = CreateMockLogger<AuthenticationFailureLimiterStartupCheck>();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "false",
        });
        var check = new AuthenticationFailureLimiterStartupCheck(
            Options.Create(new AuthenticationFailureLimiterOptions { Enabled = true }),
            configuration,
            logger);

        await check.StartAsync(CancellationToken.None);

        var warning = FindLoggedEvent(logger, eventId: 4122);
        warning.Should().NotBeNull();
        warning![0].Should().Be(LogLevel.Warning);
        warning[2]!.ToString().Should().Contain("ForwardedHeaders");
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task AuthenticationFailureLimiterStartupCheck_ForwardedHeadersEnabled_LogsNoWarning()
    {
        var logger = CreateMockLogger<AuthenticationFailureLimiterStartupCheck>();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
        });
        var check = new AuthenticationFailureLimiterStartupCheck(
            Options.Create(new AuthenticationFailureLimiterOptions { Enabled = true }),
            configuration,
            logger);

        await check.StartAsync(CancellationToken.None);

        FindLoggedEvent(logger, eventId: 4122).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task AuthenticationFailureLimiterStartupCheck_LimiterDisabled_LogsNoWarning()
    {
        var logger = CreateMockLogger<AuthenticationFailureLimiterStartupCheck>();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "false",
        });
        var check = new AuthenticationFailureLimiterStartupCheck(
            Options.Create(new AuthenticationFailureLimiterOptions { Enabled = false }),
            configuration,
            logger);

        await check.StartAsync(CancellationToken.None);

        FindLoggedEvent(logger, eventId: 4122).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void ConfigureForwardedHeaders_InvalidKnownNetwork_ThrowsActionableDiagnostic()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownNetworks:0"] = "not-a-cidr",
        });

        var configure = () => StartupConfigurationHelpers.ConfigureForwardedHeaders(
            new ServiceCollection(),
            configuration);

        configure.Should().Throw<InvalidOperationException>()
            .WithMessage("*ForwardedHeaders:KnownNetworks*not-a-cidr*");
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void ConfigureForwardedHeaders_Disabled_ReturnsFalse()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "false",
        });

        var enabled = StartupConfigurationHelpers.ConfigureForwardedHeaders(
            new ServiceCollection(),
            configuration);

        enabled.Should().BeFalse();
    }

    private static PostgresException BuildPostgresException(string sqlState, string message = "test")
        => new(messageText: message, severity: "ERROR", invariantSeverity: "ERROR", sqlState: sqlState);

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ILogger<T> CreateMockLogger<T>()
    {
        var logger = Substitute.For<ILogger<T>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        return logger;
    }

    private static FileBackedLicenseService CreateLicenseService(
        LicenseOptions options,
        ILogger<FileBackedLicenseService>? logger = null,
        IEnumerable<ILicenseContentSecretResolver>? resolvers = null)
        => new(
            Options.Create(options),
            new BouncyCastleEd25519Verifier(),
            logger ?? NullLogger<FileBackedLicenseService>.Instance,
            resolvers);

    private static object?[]? FindLoggedEvent<T>(ILogger<T> logger, int eventId)
        => logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(call => call.GetArguments())
            .FirstOrDefault(arguments => arguments[1] is EventId id && id.Id == eventId);

    private static void AssertNoLogContains<T>(ILogger<T> logger, string forbidden)
    {
        var messages = logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(call => call.GetArguments()[2]?.ToString() ?? string.Empty);

        messages.Should().AllSatisfy(message => message.Should().NotContain(forbidden));
    }
}
