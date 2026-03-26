// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Admin.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Infrastructure.Security;

[Trait("Category", "Unit")]
[Trait("Component", "Security")]
public sealed class ConfigurationValidationServiceTests
{
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

    private static IConfiguration BuildConfiguration(IDictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=honua;Username=honua;Password=secret",
            ["HONUA_ADMIN_PASSWORD"] = "StrongAdminPassword123!",
            ["Security:ConnectionEncryption:MasterKey"] = "0123456789abcdef0123456789abcdef",
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
