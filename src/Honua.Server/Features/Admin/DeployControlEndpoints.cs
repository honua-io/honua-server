// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for deploy coordination primitives.
/// </summary>
internal static class DeployControlEndpoints
{
    public static void MapDeployControlEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/deploy")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Deploy")
            .RequireAdminAuthorization();

        group.MapGet("/preflight", HandleGetDeployPreflight)
            .WithDisplayName("Get Deploy Preflight")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<DeployPreflightResponse>();
    }

    private static async Task<IResult> HandleGetDeployPreflight(
        [FromServices] IDeployPreflightProbe deployPreflightProbe,
        [FromServices] IOptions<DeploymentOptions> deploymentOptions,
        [FromServices] IHostEnvironment hostEnvironment,
        HttpContext context)
    {
        var snapshot = await deployPreflightProbe.ProbeAsync(context.RequestAborted).ConfigureAwait(false);

        var response = new DeployPreflightResponse
        {
            Status = snapshot.Status,
            ReadyForCoordinatedDeploy = snapshot.ReadyForCoordinatedDeploy,
            Message = snapshot.Message,
            ServerVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
            Environment = hostEnvironment.EnvironmentName,
            DeploymentMode = deploymentOptions.Value.Mode.ToString(),
            InstanceName = Environment.MachineName,
            GeneratedAt = DateTimeOffset.UtcNow,
            Readiness = new DeployPreflightReadiness
            {
                IsReady = snapshot.Readiness.IsReady,
                StatusCode = snapshot.Readiness.StatusCode,
                Message = snapshot.Readiness.Message
            },
            Migration = new DeployPreflightMigration
            {
                LifecycleStatus = snapshot.Migration.LifecycleStatus,
                Message = snapshot.Migration.Message,
                PlanAvailable = snapshot.Migration.PlanAvailable,
                UpgradeRequired = snapshot.Migration.UpgradeRequired,
                PendingScripts = snapshot.Migration.PendingScripts,
                ExecutedButNotDiscoveredScripts = snapshot.Migration.ExecutedButNotDiscoveredScripts,
                PlanError = snapshot.Migration.PlanError
            }
        };

        return Results.Json(response, DeployControlJsonContext.Default.DeployPreflightResponse);
    }
}
