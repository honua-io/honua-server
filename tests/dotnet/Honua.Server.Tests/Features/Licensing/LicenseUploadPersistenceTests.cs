// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Licensing;

public class LicenseUploadPersistenceTests
{
    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("inline")]
    [InlineData("secret")]
    [InlineData("file")]
    public async Task SuccessfulUpload_SurvivesRestartAndDisablingFurtherUploads(string source)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var oldLicense = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Pro,
                expiresAt: DateTimeOffset.UtcNow.AddDays(10));
            var replacement = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Enterprise,
                expiresAt: DateTimeOffset.UtcNow.AddDays(30));
            var options = CreateOptions(Path.Join(directory.FullName, "license.json"), oldLicense.PublicKeySetting);
            var resolver = new SecretResolver(Encoding.UTF8.GetString(oldLicense.LicenseData));
            if (source == "inline")
            {
                options.LicenseContent = Encoding.UTF8.GetString(oldLicense.LicenseData);
            }
            else if (source == "secret")
            {
                options.LicenseContentSecretRef = "test:license";
            }
            else
            {
                await File.WriteAllBytesAsync(options.LicensePath!, oldLicense.LicenseData);
            }

            using var service = CreateService(options, resolver);
            await service.StartAsync(CancellationToken.None);
            Assert.Equal(HonuaEdition.Pro, service.GetSnapshot().Edition);
            using var upload = new MemoryStream(replacement.LicenseData);
            Assert.True((await service.UploadLicenseAsync(upload)).Success);
            Assert.Equal(HonuaEdition.Enterprise, service.GetSnapshot().Edition);
            Assert.Equal(replacement.LicenseData, await File.ReadAllBytesAsync(options.LicensePath!));

            options.AllowAdminUpload = false;
            using var restarted = CreateService(options, resolver);
            await restarted.StartAsync(CancellationToken.None);

            Assert.Equal(HonuaEdition.Enterprise, restarted.GetSnapshot().Edition);
            Assert.Equal(source == "secret" ? 1 : 0, resolver.Calls);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task ExistingFileWithoutUpload_DoesNotDisplaceInlineConfiguration()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var inline = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Pro);
            var file = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Enterprise);
            var options = CreateOptions(Path.Join(directory.FullName, "license.json"), inline.PublicKeySetting);
            options.LicenseContent = Encoding.UTF8.GetString(inline.LicenseData);
            await File.WriteAllBytesAsync(options.LicensePath!, file.LicenseData);

            using var service = CreateService(options);
            await service.StartAsync(CancellationToken.None);

            Assert.Equal(HonuaEdition.Pro, service.GetSnapshot().Edition);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(LicenseValidationState.InvalidSignature)]
    [InlineData(LicenseValidationState.Expired)]
    [InlineData(LicenseValidationState.Malformed)]
    public async Task InvalidUploadedOverride_FailsClosedInsteadOfRestoringInlineLicense(LicenseValidationState state)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var inline = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Pro);
            var options = CreateOptions(Path.Join(directory.FullName, "license.json"), inline.PublicKeySetting);
            options.Edition = HonuaEdition.Pro;
            options.LicenseContent = Encoding.UTF8.GetString(inline.LicenseData);
            var invalid = state == LicenseValidationState.Malformed
                ? Encoding.UTF8.GetBytes("not a license")
                : LicenseTestSupport.CreateSignedLicense(HonuaEdition.Enterprise,
                    expiresAt: state == LicenseValidationState.Expired
                        ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(1),
                    tamperSignature: state == LicenseValidationState.InvalidSignature).LicenseData;
            await File.WriteAllBytesAsync(options.LicensePath + ".uploaded", invalid);

            using var service = CreateService(options);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            Assert.Contains(state == LicenseValidationState.Expired ? "license expired" : "license invalid", error.Message);

            Assert.Equal(HonuaEdition.Pro, service.GetSnapshot().Edition);
            Assert.False(service.GetSnapshot().IsValid);
            Assert.Equal(state, service.GetSnapshot().ValidationState);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task BootstrapSnapshot_HonorsUploadedOverride()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var inline = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Pro);
            var uploaded = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Enterprise);
            var options = CreateOptions(Path.Join(directory.FullName, "license.json"), inline.PublicKeySetting);
            using var service = CreateService(options);
            await service.ApplyLicenseAsync(uploaded.LicenseData);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Licensing:LicensePath"] = options.LicensePath,
                    ["Licensing:LicenseContent"] = Encoding.UTF8.GetString(inline.LicenseData),
                    [$"Licensing:TrustedKeys:{LicenseTestSupport.KeyId}"] = inline.PublicKeySetting,
                }).Build();

            var snapshot = await FileBackedLicenseService.LoadBootstrapSnapshotAsync(
                configuration, NullLoggerFactory.Instance);

            Assert.Equal(HonuaEdition.Enterprise, snapshot.Edition);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task FailedOverrideWrite_DoesNotChangeFallbackFileOrActiveLicense()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var original = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Pro);
            var uploaded = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Enterprise);
            var options = CreateOptions(Path.Join(directory.FullName, "license.json"), original.PublicKeySetting);
            await File.WriteAllBytesAsync(options.LicensePath!, original.LicenseData);
            using var service = CreateService(options);
            await service.StartAsync(CancellationToken.None);
            Directory.CreateDirectory(options.LicensePath + ".uploaded");

            using var stream = new MemoryStream(uploaded.LicenseData);
            Assert.False((await service.UploadLicenseAsync(stream)).Success);
            Assert.Equal(HonuaEdition.Pro, service.GetSnapshot().Edition);
            Assert.Equal(original.LicenseData, await File.ReadAllBytesAsync(options.LicensePath!));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task FailedMirrorWrite_ReturnsSuccessForCommittedOverrideAndSurvivesRestart()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var uploaded = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Enterprise);
            var options = CreateOptions(Path.Join(directory.FullName, "license.json"), uploaded.PublicKeySetting);
            Directory.CreateDirectory(options.LicensePath!);
            using var service = CreateService(options);
            using var stream = new MemoryStream(uploaded.LicenseData);

            var result = await service.UploadLicenseAsync(stream);

            Assert.True(result.Success);
            Assert.Contains("mirror could not be updated", result.Message);
            using var restarted = CreateService(options);
            await restarted.StartAsync(CancellationToken.None);
            Assert.Equal(HonuaEdition.Enterprise, restarted.GetSnapshot().Edition);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task ConcurrentUploads_LeaveSnapshotAndRestartOnSameLicense()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var licenses = Enumerable.Range(1, 12).Select(day => LicenseTestSupport.CreateSignedLicense(
                HonuaEdition.Enterprise, expiresAt: DateTimeOffset.UtcNow.AddDays(day))).ToArray();
            var options = CreateOptions(Path.Join(directory.FullName, "license.json"), licenses[0].PublicKeySetting);
            using var service = CreateService(options);
            await Task.WhenAll(licenses.Select(async license =>
            {
                using var stream = new MemoryStream(license.LicenseData);
                Assert.True((await service.UploadLicenseAsync(stream)).Success);
            }));

            using var restarted = CreateService(options);
            await restarted.StartAsync(CancellationToken.None);
            Assert.Equal(service.GetSnapshot().ExpiresAt, restarted.GetSnapshot().ExpiresAt);
            Assert.Equal(await File.ReadAllBytesAsync(options.LicensePath!),
                await File.ReadAllBytesAsync(options.LicensePath + ".uploaded"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static LicenseOptions CreateOptions(string path, string publicKey) => new()
    {
        LicensePath = path,
        AllowAdminUpload = true,
        TrustedKeys = new() { [LicenseTestSupport.KeyId] = publicKey },
    };

    private static FileBackedLicenseService CreateService(LicenseOptions options, SecretResolver? resolver = null) =>
        new(Options.Create(options), new BouncyCastleEd25519Verifier(),
            NullLogger<FileBackedLicenseService>.Instance, resolver is null ? null : [resolver]);

    private sealed class SecretResolver(string content) : ILicenseContentSecretResolver
    {
        public int Calls { get; private set; }
        public bool CanResolve(string? secretReference) => secretReference == "test:license";
        public Task<string?> ResolveLicenseContentAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<string?>(content);
        }
    }
}
