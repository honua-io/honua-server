// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Diagnostics;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Licensing;

public sealed partial class LicenseFailureContractTests
{
    [UnitTest]
    public async Task ExpiredProfessionalAlias_ReportsExpiredProLicense()
    {
        var key = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(new Org.BouncyCastle.Security.SecureRandom());
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "honua.license/v1", edition = "professional",
            licenseId = "synthetic-professional-alias", licensedTo = "Synthetic operator",
            issuedAt = DateTimeOffset.UtcNow.AddDays(-2), expiresAt = DateTimeOffset.UtcNow.AddDays(-1),
            entitlements = Array.Empty<string>()
        });
        var signer = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
        signer.Init(true, key);
        signer.BlockUpdate(payload, 0, payload.Length);
        var envelope = System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1, keyId = "synthetic-alias-key",
            payload = LicenseTestSupport.Base64Url(payload), signature = LicenseTestSupport.Base64Url(signer.GenerateSignature())
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Licensing:Edition"] = "Pro", ["Licensing:LicenseContent"] = envelope,
            ["Licensing:TrustedKeys:synthetic-alias-key"] = "base64url:" + LicenseTestSupport.Base64Url(key.GeneratePublicKey().GetEncoded())
        }).Build();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FileBackedLicenseService.LoadBootstrapSnapshotAsync(config, NullLoggerFactory.Instance));
        Assert.Equal("Honua Pro startup refused: license expired. Install a valid Pro license in the configured licensing source and restart. Community fallback is disabled.", error.Message);
    }

    [Theory]
    [InlineData("Pro", "missing")]
    [InlineData("Enterprise", "missing")]
    [InlineData("Pro", "invalid")]
    [InlineData("Enterprise", "invalid")]
    [InlineData("Pro", "expired")]
    [InlineData("Enterprise", "expired")]
    [Trait("Tier", "Fast")]
    public async Task StartAsync_UnusablePaidLicense_RefusesStartupWithStateAndRemedy(string edition, string state)
    {
        var license = LicenseTestSupport.CreateSignedLicense(
            Enum.Parse<HonuaEdition>(edition),
            expiresAt: DateTimeOffset.UtcNow.AddDays(state == "expired" ? -1 : 60),
            tamperSignature: state == "invalid");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Licensing:Edition"] = edition,
            ["Licensing:LicenseContent"] = state == "missing" ? null : Encoding.UTF8.GetString(license.LicenseData),
            [$"Licensing:TrustedKeys:{LicenseTestSupport.KeyId}"] = license.PublicKeySetting
        }).Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FileBackedLicenseService.LoadBootstrapSnapshotAsync(configuration, NullLoggerFactory.Instance));

        Assert.Equal($"Honua {edition} startup refused: license {state}. Install a valid {edition} license in the configured licensing source and restart. Community fallback is disabled.", error.Message);

        // Exercise the actual server entry point as well as the bootstrap seam: a failed
        // hosted-service start must become a non-zero process exit with the documented error.
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var licensePath = Path.Join(directory.FullName, "synthetic-license.json");
            if (state != "missing")
            {
                await File.WriteAllBytesAsync(licensePath, license.LicenseData);
            }
            var testAssembly = typeof(LicenseFailureContractTests).Assembly.Location;
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".runtimeconfig.json"));
            start.ArgumentList.Add("--depsfile");
            start.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".deps.json"));
            start.ArgumentList.Add(typeof(Honua.Server.Startup.LicensingRegistration).Assembly.Location);
            foreach (var key in start.Environment.Keys.Where(key =>
                key.StartsWith("Licensing", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                start.Environment.Remove(key);
            }
            start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            start.Environment["Licensing__Edition"] = edition;
            start.Environment["Licensing__LicensePath"] = licensePath;
            start.Environment[$"Licensing__TrustedKeys__{LicenseTestSupport.KeyId}"] = license.PublicKeySetting;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Assert.NotEqual(0, process.ExitCode);
                Assert.Contains(error.Message, await output + await errors, StringComparison.Ordinal);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [UnitTest]
    public async Task StartAsync_CommunityWithoutLicense_RemainsAvailable()
    {
        var service = new FileBackedLicenseService(Options.Create(new LicenseOptions()),
            new BouncyCastleEd25519Verifier(), NullLogger<FileBackedLicenseService>.Instance);
        await service.StartAsync(CancellationToken.None);
        Assert.Equal(HonuaEdition.Community, service.GetSnapshot().Edition);
        Assert.True(service.GetSnapshot().HasEntitlement("temporal.filtering"));
        await service.StopAsync(CancellationToken.None);
    }
}
