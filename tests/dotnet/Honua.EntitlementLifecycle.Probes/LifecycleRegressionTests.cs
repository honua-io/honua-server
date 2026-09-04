using System.Text;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.MultiTenancy;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.EntitlementLifecycle.Probes;

public sealed class LifecycleRegressionTests
{
    [Theory]
    [InlineData("acme-east", "acme_east")]
    [InlineData("acme.east", "acme:east")]
    public void DistinctTenantIds_MustNotShareDerivedSchema(string first, string second)
    {
        var resolver = new TenantSchemaResolver(Options.Create(new TenantSchemaOptions()));
        Assert.True(resolver.TryResolveSchema(first, out var firstSchema));
        Assert.True(resolver.TryResolveSchema(second, out var secondSchema));
        Assert.NotEqual(firstSchema, secondSchema);
    }

    [Fact]
    public async Task SuccessfulLicenseUpload_MustSurviveRestartWithInlineConfiguration()
    {
        var directory = Directory.CreateTempSubdirectory("honua-license-probe-");
        try
        {
            var original = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Pro,
                entitlements: ["analytics.clustering"]);
            var replacement = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Enterprise);
            var options = new LicenseOptions
            {
                LicensePath = Path.Join(directory.FullName, "license.json"),
                LicenseContent = Encoding.UTF8.GetString(original.LicenseData),
                AllowAdminUpload = true,
                TrustedKeys = new Dictionary<string, string>
                {
                    [LicenseTestSupport.KeyId] = original.PublicKeySetting
                }
            };
            var service = CreateService(options);
            await service.StartAsync(CancellationToken.None);
            using var upload = new MemoryStream(replacement.LicenseData);
            var result = await service.UploadLicenseAsync(upload);
            Assert.True(result.Success);
            Assert.Equal(HonuaEdition.Enterprise, service.GetSnapshot().Edition);
            Assert.Equal(replacement.LicenseData, await File.ReadAllBytesAsync(options.LicensePath));

            var restarted = CreateService(options);
            await restarted.StartAsync(CancellationToken.None);
            Assert.Equal(HonuaEdition.Enterprise, restarted.GetSnapshot().Edition);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static FileBackedLicenseService CreateService(LicenseOptions options) =>
        new(Options.Create(options), new BouncyCastleEd25519Verifier(),
            NullLogger<FileBackedLicenseService>.Instance);
}
