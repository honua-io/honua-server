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

    [Theory]
    [InlineData("serve.geoservices-imageserver")]
    [InlineData("serve.wmts")]
    [InlineData("serve.ogc-api-edr")]
    [InlineData("serve.ogc-api-coverages")]
    public void PreviewRuling_MustNotPublishImplementedGaEvidence(string key)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "Honua.sln")))
        {
            root = root.Parent;
        }
        Assert.NotNull(root);
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Join(root.FullName, "docs/gis/data/capability-matrix.v1.json")));
        var capability = manifest.RootElement.GetProperty("capabilities")
            .EnumerateArray().Single(row => row.GetProperty("key").GetString() == key);
        var maturity = capability.GetProperty("maturity");
        var implemented = maturity.TryGetProperty("implemented", out var count) ? count.GetInt32() : 0;
        Assert.Equal(0, implemented);
    }

    [Theory]
    [InlineData(false, false, LicenseValidationState.Valid)]
    [InlineData(true, false, LicenseValidationState.Expired)]
    [InlineData(false, true, LicenseValidationState.InvalidSignature)]
    public async Task Control_SignedLicenseValidationEnforcesExpiryAndSignature(
        bool expired, bool tampered, LicenseValidationState expected)
    {
        var license = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Pro,
            expiresAt: expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(1),
            tamperSignature: tampered);
        var service = CreateService(new LicenseOptions
        {
            LicenseContent = Encoding.UTF8.GetString(license.LicenseData),
            TrustedKeys = new Dictionary<string, string>
            {
                [LicenseTestSupport.KeyId] = license.PublicKeySetting
            }
        });
        await service.StartAsync(CancellationToken.None);
        Assert.Equal(expected, service.GetSnapshot().ValidationState);
        Assert.Equal(expected == LicenseValidationState.Valid,
            service.CheckEntitlement("analytics.clustering").IsActive);
    }

    [Fact]
    public void Control_DistinctSimpleTenantIdsRemainSeparate()
    {
        var resolver = new TenantSchemaResolver(Options.Create(new TenantSchemaOptions()));
        Assert.True(resolver.TryResolveSchema("alpha", out var first));
        Assert.True(resolver.TryResolveSchema("beta", out var second));
        Assert.NotEqual(first, second);
    }

    private static FileBackedLicenseService CreateService(LicenseOptions options) =>
        new(Options.Create(options), new BouncyCastleEd25519Verifier(),
            NullLogger<FileBackedLicenseService>.Instance);
}
