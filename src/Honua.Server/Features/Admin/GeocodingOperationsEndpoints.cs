// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for geocoding provider health and configuration visibility.
/// </summary>
internal static class GeocodingOperationsEndpoints
{
    private const string ProviderHealthCheckFailedMessage = "Geocoding provider health check failed.";
    public static void MapGeocodingOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/operations/geocoding")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Operations")
            .RequireAdminAuthorization();

        group.MapGet("/providers", HandleGetProviderStatus)
            .WithDisplayName("Get Geocoding Provider Status")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<GeocodingOperationsProvidersResponse>>();

        group.MapGet("/configuration", HandleGetGeocodingConfiguration)
            .WithDisplayName("Get Geocoding Configuration")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<GeocodingConfigurationResponse>>();
    }

    private static async Task<IResult> HandleGetProviderStatus(
        [FromServices] IGeocodeProviderCoordinator coordinator,
        [FromServices] IGeocodeProviderRegistry registry,
        [FromServices] IOptions<GeocodingConfiguration> geocodingConfig,
        HttpContext context,
        ILogger<GeocodingOperationsEndpointsLog> logger)
    {
        try
        {
            var healthResults = await coordinator.CheckAllProvidersHealthAsync(context.RequestAborted).ConfigureAwait(false);
            var allProviders = registry.GetAllProviders();

            var providerStatuses = healthResults.Select(health =>
            {
                var provider = allProviders.FirstOrDefault(p =>
                    string.Equals(p.Name, health.ProviderName, StringComparison.OrdinalIgnoreCase));

                return new GeocodingOperationsProviderStatusResponse
                {
                    ProviderName = health.ProviderName,
                    IsHealthy = health.IsHealthy,
                    ErrorMessage = health.ErrorMessage,
                    LastChecked = health.LastChecked,
                    ResponseTimeMs = health.ResponseTimeMs,
                    Capabilities = provider is not null ? MapCapabilities(provider.Capabilities) : null
                };
            }).ToArray();

            AdminLog.GeocodingProviderHealthChecked(logger, providerStatuses.Length);

            var config = geocodingConfig.Value;
            var response = new GeocodingOperationsProvidersResponse
            {
                Providers = providerStatuses,
                DefaultProvider = config.DefaultProvider,
                FailoverEnabled = config.EnableFailover,
                GeneratedAt = DateTimeOffset.UtcNow
            };

            return Results.Json(
                ApiResponse<GeocodingOperationsProvidersResponse>.CreateSuccess(response),
                GeocodingOperationsJsonContext.Default.ApiResponseGeocodingOperationsProvidersResponse);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AdminLog.GeocodingProviderHealthCheckFailed(logger, ex);

            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status500InternalServerError,
                ProviderHealthCheckFailedMessage);
        }
    }

    private static IResult HandleGetGeocodingConfiguration(
        [FromServices] IOptions<GeocodingConfiguration> geocodingConfig,
        ILogger<GeocodingOperationsEndpointsLog> logger)
    {
        var config = geocodingConfig.Value;

        var response = new GeocodingConfigurationResponse
        {
            Enabled = config.Enabled,
            DefaultProvider = config.DefaultProvider,
            EnableFailover = config.EnableFailover,
            MaxFailoverAttempts = config.MaxFailoverAttempts,
            EnableCaching = config.EnableCaching,
            CacheExpirationMinutes = config.CacheExpirationMinutes,
            DefaultTimeoutSeconds = config.DefaultTimeoutSeconds
        };

        return Results.Json(
            ApiResponse<GeocodingConfigurationResponse>.CreateSuccess(response),
            GeocodingOperationsJsonContext.Default.ApiResponseGeocodingConfigurationResponse);
    }

    private static GeocodingOperationsCapabilitiesResponse MapCapabilities(GeocodeProviderCapabilities capabilities)
    {
        return new GeocodingOperationsCapabilitiesResponse
        {
            SupportsForwardGeocode = capabilities.SupportsForwardGeocode,
            SupportsReverseGeocode = capabilities.SupportsReverseGeocode,
            SupportsSuggest = capabilities.SupportsSuggest,
            SupportsBatch = capabilities.SupportsBatch,
            SupportsStructuredInput = capabilities.SupportsStructuredInput,
            MaxResultsPerRequest = capabilities.MaxResultsPerRequest,
            MaxBatchSize = capabilities.MaxBatchSize,
            RateLimitPerMinute = capabilities.RateLimitPerMinute,
            RequiresAuthentication = capabilities.RequiresAuthentication
        };
    }
}

/// <summary>
/// Log category for geocoding operations endpoints.
/// </summary>
internal sealed class GeocodingOperationsEndpointsLog;
