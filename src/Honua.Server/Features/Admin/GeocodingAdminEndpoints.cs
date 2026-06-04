// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for geocoding provider health and capabilities.
/// </summary>
internal static class GeocodingAdminEndpoints
{
    internal sealed class GeocodingAdminEndpointsLog;

    public static void MapGeocodingAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/geocoding")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Geocoding")
            .RequireAdminAuthorization();

        group.MapGet("/providers", HandleGetProviders)
            .WithDisplayName("Get Geocoding Providers")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<GeocodingProvidersResponse>>();
    }

    private static async Task<IResult> HandleGetProviders(
        [FromServices] IGeocodeProviderCoordinator coordinator,
        [FromServices] ILogger<GeocodingAdminEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        AdminLog.GeocodingProviderHealthQueried(logger);

        var providers = coordinator.GetAllProviders();
        var healthResults = await coordinator.CheckAllProvidersHealthAsync(cancellationToken).ConfigureAwait(false);
        var healthLookup = healthResults.ToDictionary(h => h.ProviderName, StringComparer.OrdinalIgnoreCase);

        var defaultProvider = coordinator.GetProvider();

        var details = providers.Select(p =>
        {
            healthLookup.TryGetValue(p.Name, out var health);
            var caps = p.Capabilities;

            return new GeocodingProviderDetail
            {
                Name = p.Name,
                IsHealthy = health?.IsHealthy ?? false,
                ErrorMessage = health?.ErrorMessage,
                LastChecked = health?.LastChecked,
                ResponseTimeMs = health?.ResponseTimeMs,
                Capabilities = new GeocodingProviderCapabilitiesDto
                {
                    SupportsForwardGeocode = caps.SupportsForwardGeocode,
                    SupportsReverseGeocode = caps.SupportsReverseGeocode,
                    SupportsSuggest = caps.SupportsSuggest,
                    SupportsBatch = caps.SupportsBatch,
                    MaxResultsPerRequest = caps.MaxResultsPerRequest,
                    RateLimitPerMinute = caps.RateLimitPerMinute,
                    RequiresAuthentication = caps.RequiresAuthentication
                }
            };
        }).ToArray();

        var response = new GeocodingProvidersResponse
        {
            DefaultProvider = defaultProvider?.Name,
            FailoverEnabled = providers.Count > 1,
            Providers = details
        };

        return Results.Json(
            ApiResponse<GeocodingProvidersResponse>.CreateSuccess(response),
            GeocodingAdminJsonContext.Default.ApiResponseGeocodingProvidersResponse);
    }
}
