// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Microsoft.Extensions.Configuration;
using Honua.TestKit.Helpers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Licensing;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.LicenseManagement)]
public sealed class FileBackedLicenseServiceTests
{
    private const int MaxLicenseFileBytes = 64 * 1024;

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
    public async Task StartAsync_InlineLicenseContent_LoadsEditionWithoutAFile()
    {
        var license = LicenseTestSupport.CreateSignedLicense(
            HonuaEdition.Pro,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            entitlements: ["analytics.clustering", "staticmap.high-dpi"]);

        var logger = new RecordingLogger<FileBackedLicenseService>();
        var service = CreateService(
            new LicenseOptions
            {
                // No LicensePath: the envelope is supplied inline (e.g. resolved from a
                // secret reference on a read-only/serverless filesystem).
                LicenseContent = System.Text.Encoding.UTF8.GetString(license.LicenseData),
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
        snapshot.HasEntitlement("analytics.clustering").Should().BeTrue();
        snapshot.HasEntitlement("staticmap.high-dpi").Should().BeTrue();
    }

    [UnitTest]
    public async Task StartAsync_InlineLicenseContent_TakesPrecedenceOverLicensePath()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var licensePath = Path.Combine(tempDirectory.FullName, "missing.honua-license.json");
        var license = LicenseTestSupport.CreateSignedLicense(
            HonuaEdition.Pro,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            entitlements: ["analytics.clustering"]);

        var logger = new RecordingLogger<FileBackedLicenseService>();
        var service = CreateService(
            new LicenseOptions
            {
                LicensePath = licensePath, // does not exist; inline content must win
                LicenseContent = System.Text.Encoding.UTF8.GetString(license.LicenseData),
                TrustedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [LicenseTestSupport.KeyId] = license.PublicKeySetting
                }
            },
            logger);

        await service.StartAsync(CancellationToken.None);

        var snapshot = service.GetSnapshot();
        snapshot.Edition.Should().Be(HonuaEdition.Pro);
        snapshot.ValidationState.Should().Be(LicenseValidationState.Valid);
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
    public async Task StartAsync_OversizedLicenseFile_PublishesMalformedCommunitySnapshot()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var licensePath = Path.Combine(tempDirectory.FullName, "license.honua-license.json");
        await File.WriteAllBytesAsync(licensePath, new byte[MaxLicenseFileBytes + 1]);
        var logger = new RecordingLogger<FileBackedLicenseService>();
        var service = CreateService(new LicenseOptions { LicensePath = licensePath }, logger);

        await service.StartAsync(CancellationToken.None);

        var snapshot = service.GetSnapshot();
        snapshot.Edition.Should().Be(HonuaEdition.Community);
        snapshot.IsValid.Should().BeFalse();
        snapshot.ValidationState.Should().Be(LicenseValidationState.Malformed);
        logger.Entries.Should().Contain(entry =>
            entry.EventId == 10002 &&
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("invalid-size", StringComparison.OrdinalIgnoreCase));
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

    [UnitTest]
    public async Task StartAsync_LicenseContentSecretRef_ResolvedSignedLicense_ActivatesPro()
    {
        // Relabel the keyId to a hyphen-free token, exactly as the demo Lambda env requires
        // (Lambda env var names cannot contain hyphens). The signature is over the payload only,
        // so relabeling the envelope keyId keeps the license valid as long as TrustedKeys maps
        // the new label to the same public key.
        const string relabeledKeyId = "honuademo2026q2";
        var license = LicenseTestSupport.CreateSignedLicense(
            HonuaEdition.Pro,
            expiresAt: DateTimeOffset.UtcNow.AddDays(365),
            entitlements: ["editing.featureserver-edits", "analytics.clustering"],
            keyId: relabeledKeyId);
        var envelopeJson = Encoding.UTF8.GetString(license.LicenseData);
        var resolver = new FakeLicenseSecretResolver(envelopeJson);

        var logger = new RecordingLogger<FileBackedLicenseService>();
        var service = CreateService(
            new LicenseOptions
            {
                LicenseContentSecretRef = "aws:secretsmanager:arn:aws:secretsmanager:us-east-1:111122223333:secret:honua-demo-demo/license-pro-AbCdEf",
                TrustedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [relabeledKeyId] = license.PublicKeySetting
                }
            },
            logger,
            resolver);

        await service.StartAsync(CancellationToken.None);

        var snapshot = service.GetSnapshot();
        snapshot.Edition.Should().Be(HonuaEdition.Pro);
        snapshot.IsValid.Should().BeTrue();
        snapshot.ValidationState.Should().Be(LicenseValidationState.Valid);
        snapshot.KeyId.Should().Be(relabeledKeyId);
        snapshot.HasEntitlement("editing.featureserver-edits").Should().BeTrue();
        service.CheckEntitlement("editing.featureserver-edits").IsActive.Should().BeTrue();
        resolver.ResolveCallCount.Should().Be(1);
        logger.Entries.Should().Contain(entry => entry.EventId == 10014 && entry.Level == LogLevel.Information);
        logger.Entries.Should().Contain(entry => entry.EventId == 10006 && entry.Level == LogLevel.Information);
    }

    [UnitTest]
    public async Task StartAsync_LicenseContentSecretRef_NoResolverRegistered_FallsBackToCommunity()
    {
        var logger = new RecordingLogger<FileBackedLicenseService>();
        var service = CreateService(
            new LicenseOptions
            {
                LicenseContentSecretRef = "aws:secretsmanager:honua-demo-demo/license-pro"
            },
            logger,
            secretResolver: null);

        await service.StartAsync(CancellationToken.None);

        var snapshot = service.GetSnapshot();
        snapshot.Edition.Should().Be(HonuaEdition.Community);
        snapshot.IsValid.Should().BeTrue();
        snapshot.ValidationState.Should().Be(LicenseValidationState.NoLicenseConfigured);
        logger.Entries.Should().Contain(entry => entry.EventId == 10010 && entry.Level == LogLevel.Warning);
    }

    [UnitTest]
    public async Task StartAsync_LicenseContentSecretRef_ResolverThrows_FallsBackToCommunity()
    {
        var resolver = new FakeLicenseSecretResolver(
            content: null,
            failure: new InvalidOperationException("secret unreachable"));
        var logger = new RecordingLogger<FileBackedLicenseService>();
        var service = CreateService(
            new LicenseOptions
            {
                LicenseContentSecretRef = "aws:secretsmanager:honua-demo-demo/license-pro"
            },
            logger,
            resolver);

        await service.StartAsync(CancellationToken.None);

        var snapshot = service.GetSnapshot();
        snapshot.Edition.Should().Be(HonuaEdition.Community);
        snapshot.IsValid.Should().BeTrue();
        snapshot.ValidationState.Should().Be(LicenseValidationState.NoLicenseConfigured);
        logger.Entries.Should().Contain(entry => entry.EventId == 10013 && entry.Level == LogLevel.Warning);
    }

    [UnitTest]
    public async Task StartAsync_LicenseContentSecretRef_UnsupportedReference_FallsBackToInlineContent()
    {
        // Resolver only understands aws:secretsmanager: refs; an env: ref is unsupported, so the
        // service must fall through to the inline LicenseContent rather than failing.
        var license = LicenseTestSupport.CreateSignedLicense(
            HonuaEdition.Pro,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            entitlements: ["analytics.clustering"]);
        var resolver = new FakeLicenseSecretResolver("ignored");
        var logger = new RecordingLogger<FileBackedLicenseService>();
        var service = CreateService(
            new LicenseOptions
            {
                LicenseContentSecretRef = "env:HONUA_LICENSE",
                LicenseContent = Encoding.UTF8.GetString(license.LicenseData),
                TrustedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [LicenseTestSupport.KeyId] = license.PublicKeySetting
                }
            },
            logger,
            resolver);

        await service.StartAsync(CancellationToken.None);

        var snapshot = service.GetSnapshot();
        snapshot.Edition.Should().Be(HonuaEdition.Pro);
        snapshot.IsValid.Should().BeTrue();
        snapshot.HasEntitlement("analytics.clustering").Should().BeTrue();
        resolver.ResolveCallCount.Should().Be(0);
        logger.Entries.Should().Contain(entry => entry.EventId == 10011 && entry.Level == LogLevel.Warning);
    }

    [UnitTest]
    public async Task LoadBootstrapSnapshotAsync_LicenseContentSecretRef_WithResolver_ActivatesProEntitlements()
    {
        // honua-server#1755: the bootstrap entitlement probe must honor a Secrets-Manager-only Pro
        // license (Licensing:LicenseContentSecretRef) just like the per-request service does, so a
        // startup gate such as the Redis-cache probe sees its caching.redis entitlement.
        const string secretRef = "aws:secretsmanager:honua/license-pro";
        var license = LicenseTestSupport.CreateSignedLicense(
            HonuaEdition.Pro,
            expiresAt: DateTimeOffset.UtcNow.AddDays(365),
            entitlements: ["caching.redis", "analytics.clustering"]);
        var envelopeJson = Encoding.UTF8.GetString(license.LicenseData);
        var resolver = new FakeLicenseSecretResolver(envelopeJson);

        var configuration = BuildLicenseConfiguration(new Dictionary<string, string?>
        {
            ["Licensing:LicenseContentSecretRef"] = secretRef,
            ["Licensing:TrustedKeys:" + LicenseTestSupport.KeyId] = license.PublicKeySetting
        });

        using var loggerFactory = LoggerFactory.Create(static builder => { });
        var snapshot = await FileBackedLicenseService.LoadBootstrapSnapshotAsync(
            configuration,
            loggerFactory,
            honorDevGrant: false,
            secretResolvers: [resolver]);

        snapshot.Edition.Should().Be(HonuaEdition.Pro);
        snapshot.IsValid.Should().BeTrue();
        snapshot.HasEntitlement("caching.redis").Should().BeTrue(
            "the SM-resolved Pro license carries caching.redis, so the bootstrap Redis-cache gate must see it");
        snapshot.HasEntitlement("analytics.clustering").Should().BeTrue();
        resolver.ResolveCallCount.Should().Be(1);
    }

    [UnitTest]
    public async Task LoadBootstrapSnapshotAsync_LicenseContentSecretRef_NoResolver_FallsBackToCommunity()
    {
        // Reproduces the pre-fix bug shape: with no resolver supplied, an SM-only Pro license
        // cannot be fetched at bootstrap and the snapshot degrades to Community (no caching.redis).
        var configuration = BuildLicenseConfiguration(new Dictionary<string, string?>
        {
            ["Licensing:LicenseContentSecretRef"] = "aws:secretsmanager:honua/license-pro"
        });

        using var loggerFactory = LoggerFactory.Create(static builder => { });
        var snapshot = await FileBackedLicenseService.LoadBootstrapSnapshotAsync(
            configuration,
            loggerFactory,
            honorDevGrant: false,
            secretResolvers: null);

        snapshot.Edition.Should().Be(HonuaEdition.Community);
        snapshot.HasEntitlement("caching.redis").Should().BeFalse();
    }

    [UnitTest]
    public async Task LoadBootstrapSnapshotAsync_InlineCommunityLicense_StillResolvesCorrectly()
    {
        // A file/inline Community license must continue to resolve correctly when a resolver is
        // present — the resolver only fires for a secret reference (none configured here).
        var license = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Community);
        var resolver = new FakeLicenseSecretResolver("should-not-be-read");

        var configuration = BuildLicenseConfiguration(new Dictionary<string, string?>
        {
            ["Licensing:LicenseContent"] = Encoding.UTF8.GetString(license.LicenseData),
            ["Licensing:TrustedKeys:" + LicenseTestSupport.KeyId] = license.PublicKeySetting
        });

        using var loggerFactory = LoggerFactory.Create(static builder => { });
        var snapshot = await FileBackedLicenseService.LoadBootstrapSnapshotAsync(
            configuration,
            loggerFactory,
            honorDevGrant: false,
            secretResolvers: [resolver]);

        snapshot.Edition.Should().Be(HonuaEdition.Community);
        snapshot.IsValid.Should().BeTrue();
        snapshot.HasEntitlement("caching.redis").Should().BeFalse(
            "caching.redis is a Pro entitlement and must stay inactive for a Community license");
        resolver.ResolveCallCount.Should().Be(0);
    }

    private static IConfiguration BuildLicenseConfiguration(Dictionary<string, string?> settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

    [UnitTest]
    public async Task UploadLicenseAsync_OversizedStream_StopsReadingAfterSizeLimit()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var licensePath = Path.Combine(tempDirectory.FullName, "license.honua-license.json");
        var logger = new RecordingLogger<FileBackedLicenseService>();
        var service = CreateService(
            new LicenseOptions
            {
                AllowAdminUpload = true,
                LicensePath = licensePath
            },
            logger);
        using var stream = new OversizedLicenseStream(MaxLicenseFileBytes * 4L);

        var result = await service.UploadLicenseAsync(stream, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("License validation failed: Malformed.");
        stream.BytesRead.Should().Be(MaxLicenseFileBytes + 1);
        File.Exists(licensePath).Should().BeFalse();
        logger.Entries.Should().Contain(entry =>
            entry.EventId == 10007 &&
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("Malformed", StringComparison.OrdinalIgnoreCase));
    }

    private static FileBackedLicenseService CreateService(
        LicenseOptions options,
        RecordingLogger<FileBackedLicenseService> logger,
        ILicenseContentSecretResolver? secretResolver = null)
        => new(
            Options.Create(options),
            new BouncyCastleEd25519Verifier(),
            logger,
            secretResolver is null ? null : [secretResolver]);

    /// <summary>
    /// Stand-in for a cloud secret store (e.g. AWS Secrets Manager) that returns the
    /// configured license envelope content for a matching reference. Mirrors the
    /// fail-safe contract of <see cref="ILicenseContentSecretResolver"/>.
    /// </summary>
    private sealed class FakeLicenseSecretResolver : ILicenseContentSecretResolver
    {
        private readonly string _prefix;
        private readonly string? _content;
        private readonly Exception? _failure;

        public FakeLicenseSecretResolver(
            string? content,
            string prefix = "aws:secretsmanager:",
            Exception? failure = null)
        {
            _content = content;
            _prefix = prefix;
            _failure = failure;
        }

        public int ResolveCallCount { get; private set; }

        public bool CanResolve(string? secretReference)
            => !string.IsNullOrWhiteSpace(secretReference)
               && secretReference.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase);

        public Task<string?> ResolveLicenseContentAsync(
            string secretReference,
            CancellationToken cancellationToken = default)
        {
            ResolveCallCount++;
            if (_failure is not null)
            {
                throw _failure;
            }

            return Task.FromResult(_content);
        }
    }

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

    private sealed class OversizedLicenseStream : Stream
    {
        private readonly long _length;

        public OversizedLicenseStream(long length)
        {
            _length = length;
        }

        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var destination = buffer.AsMemory(offset, count);
            return ReadNext(destination);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ReadNext(buffer));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int ReadNext(Memory<byte> buffer)
        {
            const int MaxPermittedReadBytes = MaxLicenseFileBytes + 1;
            if (BytesRead >= MaxPermittedReadBytes)
            {
                throw new InvalidOperationException("The license stream was read past the size guard.");
            }

            var remaining = Math.Min(_length - BytesRead, MaxPermittedReadBytes - BytesRead);
            var bytesRead = (int)Math.Min(buffer.Length, remaining);
            buffer.Span[..bytesRead].Fill((byte)'x');
            BytesRead += bytesRead;
            return bytesRead;
        }
    }

    private sealed record LogEntry(LogLevel Level, int EventId, string Message);
}
