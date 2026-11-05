// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

internal static class FeatureOverviewEndpoints
{
    internal sealed class FeatureOverviewEndpointsLog;

    public static void MapFeatureOverviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/features")
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
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
        [FromServices] IOptions<McpPublishedOperationOptions> publishedOperations,
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

        var capabilities = ProjectCapabilities(
            capabilityRegistry.All,
            gateContext,
            publishedOperations.Value);

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

    internal static CapabilityOverviewItem[] ProjectCapabilities(
        IEnumerable<CapabilityDescriptor> descriptors,
        CapabilityGateContext context,
        McpPublishedOperationOptions? publishedOperations = null)
    {
        publishedOperations ??= new McpPublishedOperationOptions();

        return descriptors.Select(descriptor =>
        {
            var resolution = CapabilityGateResolver.Resolve(descriptor, context);
            var enabled = resolution.Enabled;
            var reasonCode = resolution.ReasonCode;
            if (enabled
                && descriptor.IsDynamic
                && (!publishedOperations.Enabled || publishedOperations.DeterministicOnly))
            {
                enabled = false;
                reasonCode = CapabilityReasonCodes.DisabledByConfiguration;
            }

            return new CapabilityOverviewItem
            {
                Id = descriptor.Id,
                Kind = descriptor.Kind.ToString(),
                Maturity = descriptor.Maturity.ToString(),
                Enabled = enabled,
                ReasonCode = reasonCode
            };
        }).ToArray();
    }
}
