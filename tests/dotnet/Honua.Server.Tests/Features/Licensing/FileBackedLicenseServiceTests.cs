// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.Infrastructure.Licensing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Licensing;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.LicenseManagement)]
public sealed class FileBackedLicenseServiceTests
{
    [UnitTest]
    public async Task StartAsync_NoLicensePath_PublishesCommunitySnapshot()
    {
        var logger = new RecordingLogger<FileBackedLicenseService>();
        var service = CreateService(new LicenseOptions(), logger);

        await service.StartAsync(CancellationToken.None);

        var snapshot = service.GetSnapshot();
        snapshot.Edition.Should().Be(HonuaEdition.Community);
        snapshot.IsValid.Should().BeTrue();
        snapshot.ValidationState.Should().Be(LicenseValidationState.NoLicenseConfigured);
        snapshot.HasEntitlement("temporal.filtering").Should().BeTrue();
        snapshot.HasEntitlement("analytics.clustering").Should().BeFalse();
        logger.Entries.Should().Contain(entry => entry.EventId == 10000 && entry.Level == LogLevel.Information);
    }

    [UnitTest]
    public async Task StartAsync_MissingLicenseFile_PublishesSafeCommunitySnapshot()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var licensePath = Path.Combine(tempDirectory.FullName, "missing.honua-license.json");
        var logger = new RecordingLogger<FileBackedLicenseService>();
        var service = CreateService(new LicenseOptions { LicensePath = licensePath }, logger);

        await service.StartAsync(CancellationToken.None);

        var snapshot = service.GetSnapshot();
        snapshot.Edition.Should().Be(HonuaEdition.Community);
        snapshot.IsValid.Should().BeFalse();
        snapshot.ValidationState.Should().Be(LicenseValidationState.MissingFile);
        snapshot.HasEntitlement("analytics.clustering").Should().BeFalse();
        logger.Entries.Should().Contain(entry => entry.EventId == 10001 && entry.Level == LogLevel.Warning);
    }

    [UnitTest]
    public async Task StartAsync_ValidSignedLicense_LoadsEditionAndEntitlements()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var licensePath = Path.Combine(tempDirectory.FullName, "license.honua-license.json");
        var license = LicenseTestSupport.CreateSignedLicense(
            HonuaEdition.Pro,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            entitlements: ["analytics.clustering", "staticmap.high-dpi"]);
        await File.WriteAllBytesAsync(licensePath, license.LicenseData);

        var logger = new RecordingLogger<FileBackedLicenseService>();
        var service = CreateService(
            new LicenseOptions
            {
                LicensePath = licensePath,
                TrustedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [LicenseTestSupport.KeyId] = license.PublicKeySetting
                }
            },
            logger);

        await service.StartAsync(CancellationToken.None);

        var snapshot = service.GetSnapshot();
        snapshot.Edition.Should().Be(HonuaEdition.Pro);
        snapshot.IsValid.Should().BeTrue();
        snapshot.ValidationState.Should().Be(LicenseValidationState.Valid);
        snapshot.LicenseId.Should().Be("lic-test-338");
        snapshot.LicensedTo.Should().Be("Honua Test Operator");
        snapshot.HasEntitlement("analytics.clustering").Should().BeTrue();
        snapshot.HasEntitlement("staticmap.high-dpi").Should().BeTrue();
        snapshot.HasEntitlement("analytics.spatial-join").Should().BeFalse();
        service.CheckEntitlement("analytics.clustering").IsActive.Should().BeTrue();
        service.CheckEntitlement("analytics.spatial-join").IsActive.Should().BeFalse();
        logger.Entries.Should().Contain(entry => entry.EventId == 10006 && entry.Level == LogLevel.Information);
    }

    [UnitTest]
    public async Task StartAsync_MalformedLicenseFile_PublishesSafeCommunitySnapshot()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var licensePath = Path.Combine(tempDirectory.FullName, "license.honua-license.json");
        await File.WriteAllTextAsync(licensePath, "not json");
        var logger = new RecordingLogger<FileBackedLicenseService>();
        var service = CreateService(new LicenseOptions { LicensePath = licensePath }, logger);

        await service.StartAsync(CancellationToken.None);

        var snapshot = service.GetSnapshot();
        snapshot.Edition.Should().Be(HonuaEdition.Community);
        snapshot.IsValid.Should().BeFalse();
        snapshot.ValidationState.Should().Be(LicenseValidationState.Malformed);
        logger.Entries.Should().Contain(entry => entry.EventId == 10002 && entry.Level == LogLevel.Warning);
    }

    [UnitTest]
    public async Task StartAsync_UnknownSigningKey_PublishesSafeCommunitySnapshot()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var licensePath = Path.Combine(tempDirectory.FullName, "license.honua-license.json");
        var license = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Pro, keyId: "unknown-key");
        await File.WriteAllBytesAsync(licensePath, license.LicenseData);
        var logger = new RecordingLogger<FileBackedLicenseService>();
        var service = CreateService(new LicenseOptions { LicensePath = licensePath }, logger);

        await service.StartAsync(CancellationToken.None);

        var snapshot = service.GetSnapshot();
        snapshot.Edition.Should().Be(HonuaEdition.Community);
        snapshot.IsValid.Should().BeFalse();
        snapshot.ValidationState.Should().Be(LicenseValidationState.UnknownKey);
        logger.Entries.Should().Contain(entry => entry.EventId == 10003 && entry.Level == LogLevel.Warning);
    }

    [UnitTest]
    public async Task StartAsync_InvalidSignature_PublishesSafeCommunitySnapshot()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var licensePath = Path.Combine(tempDirectory.FullName, "license.honua-license.json");
        var license = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Pro, tamperSignature: true);
        await File.WriteAllBytesAsync(licensePath, license.LicenseData);
        var logger = new RecordingLogger<FileBackedLicenseService>();
        var service = CreateService(
            new LicenseOptions
            {
                LicensePath = licensePath,
                TrustedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [LicenseTestSupport.KeyId] = license.PublicKeySetting
                }
            },
            logger);

        await service.StartAsync(CancellationToken.None);

        var snapshot = service.GetSnapshot();
        snapshot.Edition.Should().Be(HonuaEdition.Community);
        snapshot.IsValid.Should().BeFalse();
        snapshot.ValidationState.Should().Be(LicenseValidationState.InvalidSignature);
        logger.Entries.Should().Contain(entry => entry.EventId == 10004 && entry.Level == LogLevel.Warning);
    }

    [UnitTest]
    public async Task StartAsync_ExpiredLicense_PublishesSafeCommunitySnapshotWithLicenseIdentity()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var licensePath = Path.Combine(tempDirectory.FullName, "license.honua-license.json");
        var license = LicenseTestSupport.CreateSignedLicense(
            HonuaEdition.Pro,
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1),
            entitlements: ["analytics.clustering"]);
        await File.WriteAllBytesAsync(licensePath, license.LicenseData);
        var logger = new RecordingLogger<FileBackedLicenseService>();
        var service = CreateService(
            new LicenseOptions
            {
                LicensePath = licensePath,
                TrustedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [LicenseTestSupport.KeyId] = license.PublicKeySetting
                }
            },
            logger);

        await service.StartAsync(CancellationToken.None);

        var snapshot = service.GetSnapshot();
        snapshot.Edition.Should().Be(HonuaEdition.Community);
        snapshot.IsValid.Should().BeFalse();
        snapshot.ValidationState.Should().Be(LicenseValidationState.Expired);
        snapshot.LicenseId.Should().Be("lic-test-338");
        snapshot.HasEntitlement("analytics.clustering").Should().BeFalse();
        logger.Entries.Should().Contain(entry => entry.EventId == 10005 && entry.Level == LogLevel.Warning);
    }

    private static FileBackedLicenseService CreateService(
        LicenseOptions options,
        RecordingLogger<FileBackedLicenseService> logger)
        => new(
            Options.Create(options),
            new BouncyCastleEd25519Verifier(),
            logger);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, eventId.Id, formatter(state, exception)));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed record LogEntry(LogLevel Level, int EventId, string Message);
}
