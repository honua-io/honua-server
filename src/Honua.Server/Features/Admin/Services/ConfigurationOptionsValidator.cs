// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Authentication;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// Comprehensive validation service for IOptions&lt;T&gt; configuration classes.
/// Validates all critical configuration options to fail fast at startup with clear error messages.
/// </summary>
internal static class ConfigurationOptionsValidator
{
    /// <summary>
    /// Validates all IOptions configuration classes for production readiness.
    /// </summary>
    /// <param name="serviceProvider">Service provider to resolve IOptions instances.</param>
    /// <param name="logger">Logger for validation messages.</param>
    /// <param name="isDevelopment">Whether running in development mode.</param>
    /// <returns>List of validation errors, empty if configuration is valid.</returns>
    public static List<string> ValidateAllOptions(
        IServiceProvider serviceProvider,
        ILogger logger,
        bool isDevelopment)
    {
        var errors = new List<string>();

        try
        {
            // Validate critical authentication options
            ValidateOidcOptions(serviceProvider, errors, isDevelopment);
            ValidateApiKeyOptions(serviceProvider, errors, isDevelopment);

            // Validate performance monitoring options
            ValidatePerformanceMonitoringOptions(serviceProvider, errors);

            // Validate adaptive sampling options
            ValidateAdaptiveSamplingOptions(serviceProvider, errors);

            if (errors.Count == 0)
            {
                ConfigurationLog.ConfigurationValidated(logger, "All IOptions configurations are valid");
            }
        }
        catch (Exception ex)
        {
            errors.Add("Configuration validation failed due to an unexpected error. Check server logs for details.");
            ConfigurationLog.ConfigurationError(logger, ex.ToString(), ex);
        }

        return errors;
    }

    private static void ValidateOidcOptions(IServiceProvider serviceProvider, List<string> errors, bool isDevelopment)
    {
        try
        {
            var oidcOptions = serviceProvider.GetRequiredService<IOptions<OidcAuthenticationOptions>>().Value;

            // Validate Azure AD provider if enabled
            if (oidcOptions.AzureAd?.Enabled == true)
            {
                var azureAd = oidcOptions.AzureAd;
                if (string.IsNullOrWhiteSpace(azureAd.TenantId))
                {
                    errors.Add("OidcAuthentication:AzureAd:TenantId is required when Azure AD authentication is enabled");
                }
                if (string.IsNullOrWhiteSpace(azureAd.ClientId))
                {
                    errors.Add("OidcAuthentication:AzureAd:ClientId is required when Azure AD authentication is enabled");
                }
                if (string.IsNullOrWhiteSpace(azureAd.Instance))
                {
                    errors.Add("OidcAuthentication:AzureAd:Instance is required when Azure AD authentication is enabled");
                }

                // Validate token validation settings
                if (oidcOptions.TokenValidation.ValidateIssuer && oidcOptions.TokenValidation.ValidIssuers.Length == 0)
                {
                    errors.Add("OidcAuthentication:TokenValidation:ValidIssuers cannot be empty when issuer validation is enabled");
                }
                if (oidcOptions.TokenValidation.ValidateAudience && oidcOptions.TokenValidation.ValidAudiences.Length == 0)
                {
                    errors.Add("OidcAuthentication:TokenValidation:ValidAudiences cannot be empty when audience validation is enabled");
                }
            }

            // Validate Google provider if enabled
            if (oidcOptions.Google?.Enabled == true)
            {
                var google = oidcOptions.Google;
                if (string.IsNullOrWhiteSpace(google.ClientId))
                {
                    errors.Add("OidcAuthentication:Google:ClientId is required when Google authentication is enabled");
                }
                if (string.IsNullOrWhiteSpace(google.ClientSecret))
                {
                    errors.Add("OidcAuthentication:Google:ClientSecret is required when Google authentication is enabled");
                }
            }

            // Validate Generic OIDC provider if enabled
            if (oidcOptions.Generic?.Enabled == true)
            {
                var generic = oidcOptions.Generic;
                if (string.IsNullOrWhiteSpace(generic.Authority))
                {
                    errors.Add("OidcAuthentication:Generic:Authority is required when generic OIDC authentication is enabled");
                }
                if (string.IsNullOrWhiteSpace(generic.ClientId))
                {
                    errors.Add("OidcAuthentication:Generic:ClientId is required when generic OIDC authentication is enabled");
                }
            }
        }
        catch (Exception)
        {
            errors.Add("Failed to validate OIDC options due to an unexpected error.");
        }
    }

    private static void ValidateApiKeyOptions(IServiceProvider serviceProvider, List<string> errors, bool isDevelopment)
    {
        try
        {
            var apiKeyOptions = serviceProvider.GetRequiredService<IOptions<ApiKeyAuthenticationOptions>>().Value;

            // In production, admin password must be set
            if (!isDevelopment && string.IsNullOrWhiteSpace(apiKeyOptions.AdminPassword))
            {
                errors.Add("HONUA_ADMIN_PASSWORD environment variable is required in production environments");
            }

            // Dev auth bypass is ignored outside development/test environments by the auth handler.

            if (!isDevelopment &&
                apiKeyOptions.EnableBasicAuthCompatibility &&
                !apiKeyOptions.RequireHttpsForBasicAuth)
            {
                errors.Add("Authentication:BasicCompatibility:RequireHttps must be true when Basic compatibility is enabled in production.");
            }
        }
        catch (Exception)
        {
            errors.Add("Failed to validate API key options due to an unexpected error.");
        }
    }

    private static void ValidatePerformanceMonitoringOptions(IServiceProvider serviceProvider, List<string> errors)
    {
        try
        {
            var perfOptions = serviceProvider.GetRequiredService<IOptions<PerformanceMonitoringOptions>>().Value;

            if (perfOptions.SlowRequestThreshold <= TimeSpan.Zero)
            {
                errors.Add("PerformanceMonitoring:SlowRequestThreshold must be greater than zero");
            }

            if (perfOptions.MemorySamplingInterval <= 0)
            {
                errors.Add("PerformanceMonitoring:MemorySamplingInterval must be greater than zero");
            }
        }
        catch (Exception)
        {
            errors.Add("Failed to validate performance monitoring options due to an unexpected error.");
        }
    }

    private static void ValidateAdaptiveSamplingOptions(IServiceProvider serviceProvider, List<string> errors)
    {
        try
        {
            var samplingOptions = serviceProvider.GetRequiredService<IOptions<AdaptiveSamplingOptions>>().Value;

            // Validate CPU thresholds
            if (samplingOptions.Load.CpuThreshold < 30.0 || samplingOptions.Load.CpuThreshold > 95.0)
            {
                errors.Add("AdaptiveSampling:Load:CpuThreshold must be between 30 and 95");
            }

            // Validate memory thresholds
            if (samplingOptions.Load.MemoryThreshold < 30.0 || samplingOptions.Load.MemoryThreshold > 95.0)
            {
                errors.Add("AdaptiveSampling:Load:MemoryThreshold must be between 30 and 95");
            }

            // Validate sampling rates
            if (samplingOptions.BaseSamplingRate < 0.0 || samplingOptions.BaseSamplingRate > 1.0)
            {
                errors.Add("AdaptiveSampling:BaseSamplingRate must be between 0.0 and 1.0");
            }

            // Validate realistic thresholds
            if (samplingOptions.Load.CpuThreshold > 90.0)
            {
                errors.Add("AdaptiveSampling:Load:CpuThreshold above 90% may never trigger in practice (consider lowering to 70-80%)");
            }

            if (samplingOptions.Load.MemoryThreshold > 90.0)
            {
                errors.Add("AdaptiveSampling:Load:MemoryThreshold above 90% may never trigger in practice (consider lowering to 80-90%)");
            }
        }
        catch (InvalidOperationException)
        {
            // AdaptiveSamplingOptions service not registered - skip validation
        }
        catch (Exception)
        {
            errors.Add("Failed to validate adaptive sampling options due to an unexpected error.");
        }
    }
}

/// <summary>
/// Extension method for adding comprehensive IOptions validation to startup.
/// </summary>
internal static class ConfigurationValidationExtensions
{
    /// <summary>
    /// Adds comprehensive validation for all IOptions&lt;T&gt; configuration classes.
    /// Call this after all configuration services are registered.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddConfigurationOptionsValidation(this IServiceCollection services)
    {
        // Add a hosted service that validates configuration at startup
        services.AddHostedService<ConfigurationValidationHostedService>();
        return services;
    }
}

/// <summary>
/// Hosted service that validates configuration at startup and fails fast if invalid.
/// </summary>
internal sealed class ConfigurationValidationHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConfigurationValidationHostedService> _logger;
    private readonly IHostEnvironment _environment;

    public ConfigurationValidationHostedService(
        IServiceProvider serviceProvider,
        ILogger<ConfigurationValidationHostedService> logger,
        IHostEnvironment environment)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _environment = environment;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var isDevelopment = _environment.IsDevelopment() || _environment.IsEnvironment("Test");
        var errors = ConfigurationOptionsValidator.ValidateAllOptions(_serviceProvider, _logger, isDevelopment);

        if (errors.Count > 0)
        {
            var errorMessage = $"Configuration validation failed with {errors.Count} error(s):\n" +
                               string.Join("\n", errors.Select(e => $"- {e}"));

            ConfigurationLog.ConfigurationValidationFailed(_logger, errors.Count, errorMessage);

            // Fail fast - don't start the application with invalid configuration
            throw new InvalidOperationException($"Application startup failed due to configuration errors:\n{errorMessage}");
        }

        ConfigurationLog.ConfigurationValidationSucceeded(_logger);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
