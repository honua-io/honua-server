// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Admin.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Server.Tests.Infrastructure.Security;

[Trait("Category", "Unit")]
[Trait("Component", "Security")]
public sealed class ConfigurationValidationServiceTests
{
    [Theory]
    [InlineData(null, "disabled (preview, default)")]
    [InlineData("Capabilities:Experimental:alerts.geofence:Enabled", "preview (opted in; workers require Alerts:Enabled)")]
    [InlineData("Capabilities:Experimental:Enabled", "preview (opted in; workers require Alerts:Enabled)")]
    public void ValidateConfiguration_AlertingLifecycle_UsesExistingStartupStatusEvent(string? flag, string expectedStatus)
    {
        var values = new Dictionary<string, string?>();
        if (flag != null)
        {
            values[flag] = "true";
        }

        var logger = new Mock<ILogger>();
        logger.Setup(instance => instance.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        ConfigurationValidationService.ValidateConfiguration(
            BuildConfiguration(values), logger.Object, isDevelopment: false);

        logger.Invocations.Should().ContainSingle(invocation =>
            invocation.Method.Name == nameof(ILogger.Log)
            && (LogLevel)invocation.Arguments[0] == LogLevel.Information
            && ((EventId)invocation.Arguments[1]).Id == 4010
            && invocation.Arguments[2].ToString()!.Contains("Customer alerting")
            && invocation.Arguments[2].ToString()!.Contains(expectedStatus));
    }

    [Fact]
    public void ValidateConfiguration_NonDevelopment_WithStrictHostValidationAndNoAllowlist_ReturnsError()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["HostValidation:RequireExplicitHosts"] = "true"
        });

        var errors = ConfigurationValidationService.ValidateConfiguration(
            configuration,
            NullLogger.Instance,
            isDevelopment: false);

        errors.Should().Contain(error =>
            error.Contains("Host validation is enabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateConfiguration_NonDevelopment_WithStrictHostValidationAndPublicBaseUrl_DoesNotReturnHostValidationError()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["HostValidation:RequireExplicitHosts"] = "true",
            ["Public:BaseUrl"] = "https://api.honua.test"
        });

        var errors = ConfigurationValidationService.ValidateConfiguration(
            configuration,
            NullLogger.Instance,
            isDevelopment: false);

        errors.Should().NotContain(error =>
            error.Contains("Host validation is enabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateConfiguration_NonDevelopment_WithStrictHostValidationAndAllowedHosts_DoesNotReturnHostValidationError()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["HostValidation:RequireExplicitHosts"] = "true",
            ["HostValidation:AllowedHosts:0"] = "api.honua.test"
        });

        var errors = ConfigurationValidationService.ValidateConfiguration(
            configuration,
            NullLogger.Instance,
            isDevelopment: false);

        errors.Should().NotContain(error =>
            error.Contains("Host validation is enabled", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null, "https://alias.honua.test", false)]
    [InlineData("", "https://alias.honua.test", false)]
    [InlineData(" \t ", "https://alias.honua.test", false)]
    [InlineData("https://primary.honua.test", "not-a-url", false)]
    [InlineData("not-a-url", "https://alias.honua.test", true)]
    [InlineData("", "ftp://alias.honua.test", true)]
    public void ValidateConfiguration_StrictHostValidation_PublicUrlAliasPreservesResolutionContract(
        string? primaryUrl, string aliasUrl, bool expectedError)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["HostValidation:RequireExplicitHosts"] = "true",
            ["Public:BaseUrl"] = primaryUrl,
            ["PUBLIC_BASE_URL"] = aliasUrl
        });

        var errors = ConfigurationValidationService.ValidateConfiguration(
            configuration, NullLogger.Instance, isDevelopment: false);

        errors.Any(error => error.Contains("Host validation is enabled", StringComparison.OrdinalIgnoreCase))
            .Should().Be(expectedError);
    }

    [Fact]
    public void ValidateConfiguration_Development_WithDevAuthEnabled_ReturnsError()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["HONUA_DEV_AUTH"] = "true"
        });

        var errors = ConfigurationValidationService.ValidateConfiguration(
            configuration,
            NullLogger.Instance,
            isDevelopment: true,
            isTest: false);

        errors.Should().Contain(error =>
            error.Contains("HONUA_DEV_AUTH", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("test environment", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("quickstart-admin-password")]
    [InlineData("scale-test-admin-password")]
    [InlineData("CiteAdminPassword123!")]
    public void ValidateConfiguration_NonDevelopment_WithShippedAdminPasswordLiteral_ReturnsError(string literal)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["HONUA_ADMIN_PASSWORD"] = literal
        });

        var errors = ConfigurationValidationService.ValidateConfiguration(
            configuration, NullLogger.Instance, isDevelopment: false);

        errors.Should().Contain(error =>
            error.Contains("HONUA_ADMIN_PASSWORD", StringComparison.Ordinal) &&
            error.Contains("placeholder", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Security:ConnectionEncryption:MasterKey", "honua-compose-dev-master-key-0123456789")]
    [InlineData("Security:ConnectionEncryption:MasterKey", "test-master-key-that-is-at-least-32-characters-long-for-security")]
    [InlineData("Security:ConnectionEncryption:Salt", "aG9udWEtY29tcG9zZS1kZXYtc2FsdC0yMDI2")]
    [InlineData("ConnectionStrings:DefaultConnection", "Host=postgres;Database=honua;Username=honua;Password=studio_receipt_password")]
    [InlineData("ConnectionStrings:honua", "Host=postgres;Database=honua;Username=honua;Password=studio_receipt_password")]
    [InlineData("Operations:SecretChannel:KeyRingCertificatePassword", "StudioReceiptKeyRing")]
    [InlineData("Oidc:TokenValidation:SymmetricSigningKey", "studio-dashboard-receipt-signing-key-2026-1")]
    public void ValidateConfiguration_NonDevelopment_WithShippedSecretLiteral_ReturnsError(string path, string literal)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [path] = literal
        });

        var errors = ConfigurationValidationService.ValidateConfiguration(
            configuration, NullLogger.Instance, isDevelopment: false);

        errors.Should().Contain(error =>
            error.Contains(path.Replace(":", "__", StringComparison.Ordinal), StringComparison.Ordinal) &&
            error.Contains("placeholder", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ValidateConfiguration_RelaxedEnvironment_WithShippedSecretLiteral_IsPermitted(bool isDevelopment, bool isTest)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["HONUA_ADMIN_PASSWORD"] = "quickstart-admin-password",
            ["Security:ConnectionEncryption:MasterKey"] = "honua-compose-dev-master-key-0123456789",
            ["Security:ConnectionEncryption:Salt"] = "aG9udWEtY29tcG9zZS1kZXYtc2FsdC0yMDI2"
        });

        var errors = ConfigurationValidationService.ValidateConfiguration(
            configuration, NullLogger.Instance, isDevelopment, isTest);

        errors.Should().NotContain(error =>
            error.Contains("placeholder", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Drift guard: every credential-shaped literal this repository ships in a compose
    /// file, an <c>.env*.example</c> file, or a CI harness default must already be denied
    /// at production startup. A new harness that introduces a fresh literal fails here
    /// instead of quietly becoming a value a deployment can keep.
    /// </summary>
    [Fact]
    public void KnownPlaceholderSecrets_CoversCredentialLiteralsInShippedFiles()
    {
        var root = FindRepositoryRoot();

        var undenied = ShippedSecretLiteralScanner
            .Scan(root)
            .Where(finding => !ConfigurationValidationService.KnownPlaceholderSecrets.Contains(finding.Value))
            .OrderBy(finding => finding.Value, StringComparer.Ordinal)
            .ThenBy(finding => finding.Location, StringComparer.Ordinal)
            .ToList();

        undenied.Should().BeEmpty(
            "every credential literal shipped in this repository must be listed in " +
            "ConfigurationValidationService.KnownPlaceholderSecrets so production startup refuses it. " +
            "Undenied: " + string.Join("; ", undenied.Select(finding =>
                $"'{finding.Value}' ({finding.Key} at {finding.Location})")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            // False positive: "Honua.sln" is a fixed relative literal, never absolute.
            if (File.Exists(Path.Join(directory.FullName, "Honua.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }

    private static IConfiguration BuildConfiguration(IDictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=honua;Username=honua;Password=Sh1pp3d-Nowhere",
            ["HONUA_ADMIN_PASSWORD"] = "StrongAdminPassword123!",
            ["Security:ConnectionEncryption:MasterKey"] = "unit-test-master-key-not-a-shipped-literal-0001",
            ["HostValidation:Enabled"] = "true"
        };

        if (overrides != null)
        {
            foreach (var (key, value) in overrides)
            {
                values[key] = value;
            }
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
