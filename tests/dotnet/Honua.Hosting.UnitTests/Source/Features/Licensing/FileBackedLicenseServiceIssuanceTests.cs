// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Licensing;

// A license's issue time is its not-before, and an upload may not roll back to an older license.
[Protocol(TestProtocols.Admin)]
[Operation(Operations.LicenseManagement)]
public sealed class FileBackedLicenseServiceIssuanceTests
{
    [UnitTest]
    public async Task StartAsync_LicenseIssuedInTheFuture_RefusesStartup()
    {
        var license = Issued(DateTimeOffset.UtcNow.AddDays(2));
        using var directory = new TemporaryLicenseDirectory();
        await File.WriteAllBytesAsync(directory.LicensePath, license.LicenseData);
        using var service = CreateService(directory.LicensePath, license.PublicKeySetting);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));

        service.GetSnapshot().IsValid.Should().BeFalse();
        service.GetSnapshot().ValidationState.Should().Be(LicenseValidationState.Malformed);
    }

    [UnitTest]
    public async Task StartAsync_LicenseIssuedWithinClockSkew_Loads()
    {
        var license = Issued(DateTimeOffset.UtcNow.AddMinutes(1));
        using var directory = new TemporaryLicenseDirectory();
        await File.WriteAllBytesAsync(directory.LicensePath, license.LicenseData);
        using var service = CreateService(directory.LicensePath, license.PublicKeySetting);

        await service.StartAsync(CancellationToken.None);

        service.GetSnapshot().IsValid.Should().BeTrue();
    }

    [UnitTest]
    public async Task UploadLicenseAsync_OlderThanTheLicenseInForce_IsRefusedAndNewerIsApplied()
    {
        var inForce = Issued(DateTimeOffset.UtcNow.AddDays(-1));
        using var directory = new TemporaryLicenseDirectory();
        await File.WriteAllBytesAsync(directory.LicensePath, inForce.LicenseData);
        using var service = CreateService(directory.LicensePath, inForce.PublicKeySetting);
        await service.StartAsync(CancellationToken.None);

        var older = await UploadAsync(service, Issued(DateTimeOffset.UtcNow.AddDays(-30)));
        var newer = await UploadAsync(service, Issued(DateTimeOffset.UtcNow.AddMinutes(-1)));

        older.Success.Should().BeFalse();
        older.Message.Should().Contain("issued before the license in force");
        newer.Success.Should().BeTrue(newer.Message);
    }

    private static SignedLicenseTestFile Issued(DateTimeOffset issuedAt)
        => LicenseTestSupport.CreateSignedLicenseWithOptions(
            HonuaEdition.Pro,
            new SignedLicenseTestOptions { IssuedAt = issuedAt, ExpiresAt = DateTimeOffset.UtcNow.AddDays(60) });

    private static async Task<LicenseUploadResult> UploadAsync(FileBackedLicenseService service, SignedLicenseTestFile license)
    {
        using var stream = new MemoryStream(license.LicenseData);
        return await service.UploadLicenseAsync(stream, CancellationToken.None);
    }

    private static FileBackedLicenseService CreateService(string licensePath, string publicKeySetting)
        => new(
            Options.Create(new LicenseOptions
            {
                AllowAdminUpload = true,
                LicensePath = licensePath,
                TrustedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [LicenseTestSupport.KeyId] = publicKeySetting,
                },
            }),
            new BouncyCastleEd25519Verifier(),
            NullLogger<FileBackedLicenseService>.Instance);

    private sealed class TemporaryLicenseDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory();

        public string LicensePath => Path.Join(_directory.FullName, "license.honua-license.json");

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
