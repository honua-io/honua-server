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

        logger.Verify(instance => instance.Log(
            LogLevel.Information,
            It.Is<EventId>(id => id.Id == 4010),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("Customer alerting")
                && state.ToString()!.Contains(expectedStatus)),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
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
