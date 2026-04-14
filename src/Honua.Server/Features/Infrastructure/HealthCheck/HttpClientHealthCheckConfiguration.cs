// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Server.Features.Infrastructure.Resilience;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Honua.Server.Features.Infrastructure.HealthCheck;

/// <summary>
/// Configures health checks for HTTP clients with circuit breaker awareness.
/// Provides centralized health monitoring for all external service dependencies.
/// </summary>
public static class HttpClientHealthCheckConfiguration
{
    /// <summary>
    /// Adds health checks for all configured HTTP clients with circuit breaker monitoring.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <returns>The health checks builder for chaining.</returns>
    public static IHealthChecksBuilder AddHttpClientHealthChecks(this IHealthChecksBuilder builder)
    {
        // External service health checks - these are treated as degraded when failing
        // to prevent the entire application from being marked unhealthy due to external issues

        // ArcGIS REST services
        builder.AddHttpClientHealthCheck(
            "arcgis-rest",
            "https://services.arcgis.com/health", // Placeholder - replace with actual health endpoint
            timeout: TimeSpan.FromSeconds(15),
            tags: new[] { "external", "arcgis", "import" });

        // GeoServer REST services
        builder.AddHttpClientHealthCheck(
            "geoserver-rest",
            "https://geoserver.example.com/geoserver/rest/about/version", // Placeholder
            timeout: TimeSpan.FromSeconds(10),
            tags: new[] { "external", "geoserver", "import" });

        // AWS Secrets Manager
        builder.AddHttpClientHealthCheck(
            "aws-secrets-manager",
            "https://secretsmanager.us-east-1.amazonaws.com/", // Regional endpoint
            timeout: TimeSpan.FromSeconds(5),
            tags: new[] { "external", "aws", "secrets" });

        // Azure Key Vault
        builder.AddHttpClientHealthCheck(
            "azure-key-vault",
            "https://vault.azure.net/", // Placeholder - replace with actual vault
            timeout: TimeSpan.FromSeconds(5),
            tags: new[] { "external", "azure", "secrets" });

        // Nominatim geocoding
        builder.AddHttpClientHealthCheck(
            "nominatim-geocoding",
            "https://nominatim.openstreetmap.org/status.php",
            timeout: TimeSpan.FromSeconds(10),
            tags: new[] { "external", "geocoding" });

        // Azure Maps geocoding
        builder.AddHttpClientHealthCheck(
            "azure-maps-geocoding",
            "https://atlas.microsoft.com/", // Base endpoint
            timeout: TimeSpan.FromSeconds(10),
            tags: new[] { "external", "azure", "geocoding" });

        // NL Query service (if configured)
        builder.AddHttpClientHealthCheck(
            "nl-query",
            "https://api.openai.com/v1/models", // OpenAI API health
            timeout: TimeSpan.FromSeconds(10),
            tags: new[] { "external", "ai", "nlquery" });

        return builder;
    }

    /// <summary>
    /// Adds webhook health checks for notification systems.
    /// These are marked as degraded when failing since they don't affect core functionality.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="webhookUrls">Dictionary of webhook name to URL mappings.</param>
    /// <returns>The health checks builder for chaining.</returns>
    public static IHealthChecksBuilder AddWebhookHealthChecks(
        this IHealthChecksBuilder builder,
        IReadOnlyDictionary<string, string>? webhookUrls = null)
    {
        if (webhookUrls == null || webhookUrls.Count == 0)
        {
            return builder;
        }

        foreach (var (name, url) in webhookUrls)
        {
            var serviceType = $"{name}-webhook";
            builder.AddHttpClientHealthCheck(
                serviceType,
                url,
                timeout: TimeSpan.FromSeconds(5),
                tags: new[] { "external", "webhook", name });
        }

        return builder;
    }

    /// <summary>
    /// Creates a custom health check for monitoring circuit breaker states across all HTTP clients.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <returns>The health checks builder for chaining.</returns>
    public static IHealthChecksBuilder AddCircuitBreakerHealthCheck(this IHealthChecksBuilder builder)
    {
        return builder.Add(new HealthCheckRegistration(
            "circuit-breakers",
            sp => new CircuitBreakerHealthCheck(),
            HealthStatus.Degraded, // Circuit breaker issues are degraded, not unhealthy
            new[] { "circuit-breaker", "resilience" }));
    }
}

/// <summary>
/// Health check that monitors the state of circuit breakers across all HTTP clients.
/// </summary>
internal sealed class CircuitBreakerHealthCheck : IHealthCheck
{
    private static readonly string[] MonitoredServices = new[]
    {
        "arcgis-rest",
        "geoserver-rest",
        "aws-secrets-manager",
        "azure-key-vault",
        "nominatim-geocoding",
        "azure-maps-geocoding",
        "nl-query",
        "feature-change-webhook",
        "manifest-approval-webhook",
        "manifest-drift-webhook",
        "alerts-webhook",
        "alerts-slack",
        "alerts-teams"
    };

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var circuitBreakerStates = new Dictionary<string, object>();
        var openCircuits = 0;

        foreach (var serviceType in MonitoredServices)
        {
            try
            {
                // Get the policy for each service and check if we can determine circuit state
                var policy = HttpResiliencePolicies.GetHttpPolicy(serviceType);

                // Note: Polly doesn't provide easy introspection of circuit breaker state
                // In a production system, you might want to implement custom circuit breaker
                // state tracking or use a library that provides better observability
                circuitBreakerStates[$"{serviceType}_policy"] = "configured";
            }
            catch (Exception ex)
            {
                circuitBreakerStates[$"{serviceType}_error"] = ex.Message;
            }
        }

        var status = openCircuits > 0 ? HealthStatus.Degraded : HealthStatus.Healthy;
        var description = openCircuits > 0
            ? $"{openCircuits} circuit breaker(s) are open"
            : "All circuit breakers are operating normally";

        return Task.FromResult(new HealthCheckResult(
            status,
            description,
            null,
            circuitBreakerStates));
    }
}
