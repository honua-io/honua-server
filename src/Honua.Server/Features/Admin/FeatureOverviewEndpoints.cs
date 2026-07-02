// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for the feature overview page showing edition-gated features.
/// </summary>
internal static class FeatureOverviewEndpoints
{
    internal sealed class FeatureOverviewEndpointsLog;

    public static void MapFeatureOverviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/features")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Features")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleGetFeatureOverview)
            .WithDisplayName("Get Feature Overview")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<FeatureOverviewResponse>>();
    }

    private static IResult HandleGetFeatureOverview(
        [FromServices] ILicenseEntitlementService entitlementService,
        [FromServices] ICapabilityRegistry capabilityRegistry,
        [FromServices] IOptions<CapabilityFlagOptions> experimentalFlags,
        [FromServices] IWebHostEnvironment hostEnvironment,
        [FromServices] ILogger<FeatureOverviewEndpointsLog> logger)
    {
        AdminLog.FeatureOverviewQueried(logger);

        var snapshot = entitlementService.GetSnapshot();

        var features = FeatureCatalog.All.Select(f =>
        {
            var isEnabled = snapshot.HasEntitlement(f.Key);
            return new FeatureOverviewItem
            {
                Key = f.Key,
                DisplayName = f.DisplayName,
                Category = f.Category,
                Description = f.Description,
                IsEnabled = isEnabled,
                MinimumEdition = f.MinimumEdition.ToString(),
                UpgradeMessage = isEnabled ? null : $"Requires {f.MinimumEdition}"
            };
        }).ToArray();

        // The capability roster is resolved for the current edition/environment
        // through the same T2 gate resolver every surface uses, so the READ view
        // shows the exact enabled/reason state the live gates enforce.
        var gateContext = new CapabilityGateContext
        {
            Edition = snapshot.Edition,
            DeploymentEnvironment = hostEnvironment.EnvironmentName,
            ExperimentalFlags = experimentalFlags.Value
        };

        var capabilities = ProjectCapabilities(capabilityRegistry.All, gateContext);

        var response = new FeatureOverviewResponse
        {
            CurrentEdition = snapshot.Edition.ToString(),
            Features = features,
            Capabilities = capabilities
        };

        return Results.Json(
            ApiResponse<FeatureOverviewResponse>.CreateSuccess(response),
            FeatureOverviewJsonContext.Default.ApiResponseFeatureOverviewResponse);
    }

    /// <summary>
    /// Projects the unified capability roster into the admin read model, resolving
    /// each descriptor's enabled/reason state through the T2
    /// <see cref="CapabilityGateResolver"/> for the given context. Extracted as a
    /// pure function so the resolution projection (including the experimental-disabled
    /// reason) is unit-testable without spinning up the endpoint.
    /// </summary>
    /// <param name="descriptors">The capability descriptors to project.</param>
    /// <param name="context">The edition/environment/flag context to resolve against.</param>
    internal static CapabilityOverviewItem[] ProjectCapabilities(
        IEnumerable<CapabilityDescriptor> descriptors,
        CapabilityGateContext context)
    {
        return descriptors.Select(descriptor =>
        {
            var resolution = CapabilityGateResolver.Resolve(descriptor, context);
            return new CapabilityOverviewItem
            {
                Id = descriptor.Id,
                Kind = descriptor.Kind.ToString(),
                Maturity = descriptor.Maturity.ToString(),
                Enabled = resolution.Enabled,
                ReasonCode = resolution.ReasonCode
            };
        }).ToArray();
    }
}
