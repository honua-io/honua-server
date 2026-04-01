// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;

namespace Honua.Server.Features.Import;

/// <summary>
/// Unified migration source scanner endpoints.
/// </summary>
internal static class MigrationScannerEndpoints
{
    /// <summary>
    /// Maps migration source scanner endpoints.
    /// </summary>
    public static void MapMigrationScannerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/import")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Import")
            .RequireAdminAuthorization();

        _ = group.MapPost("/scan", HandleScanSource)
            .WithName("ScanMigrationSource")
            .WithSummary("Scan a supported migration source and return a deterministic inventory artifact");
    }

    private static async Task HandleScanSource(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        MigrationInventoryScanRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                ImportJsonContext.Default.MigrationInventoryScanRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Invalid request body", StatusCodes.Status400BadRequest);
            return;
        }

        if (request == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Request body is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (!TryNormalizeSourceKind(request.SourceKind, out var sourceKind))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "SourceKind must be one of: geoserver, geoserver-rest, geoservices, arcgis-geoservices-rest.",
                StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.SourceUrl))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "SourceUrl is required", StatusCodes.Status400BadRequest);
            return;
        }

        try
        {
            MigrationSourceInventoryArtifact artifact;

            switch (sourceKind)
            {
                case "geoserver-rest":
                {
                    var allowUnsafeLocalUrls = GeoServerImportExecutionSettings.ShouldAllowUnsafeLocalUrls(context.RequestServices);
                    var validation = await GeoServerServiceUrlValidation.ValidateAsync(
                        request.SourceUrl,
                        allowUnsafeLocalUrls,
                        cancellationToken).ConfigureAwait(false);
                    if (!validation.IsValid)
                    {
                        await AdminResponseWriter.WriteErrorAsync(
                            context,
                            validation.ErrorMessage!,
                            StatusCodes.Status400BadRequest);
                        return;
                    }

                    var importService = context.RequestServices.GetRequiredService<IGeoServerImportService>();
                    artifact = await importService.ScanSourceAsync(
                        new GeoServerDiscoveryRequest
                        {
                            GeoServerRestUrl = request.SourceUrl,
                            Username = request.Username,
                            Password = request.Password,
                            TimeoutSeconds = request.TimeoutSeconds ?? 120,
                            IncludeCompatibilityAnalysis = true,
                            IncludeStyleContent = request.IncludeStyleContent ?? false
                        },
                        cancellationToken).ConfigureAwait(false);
                    break;
                }
                default:
                {
                    var validation = await GeoservicesServiceUrlValidation.ValidateAsync(
                        request.SourceUrl,
                        cancellationToken).ConfigureAwait(false);
                    if (!validation.IsValid)
                    {
                        await AdminResponseWriter.WriteErrorAsync(
                            context,
                            validation.ErrorMessage!,
                            StatusCodes.Status400BadRequest);
                        return;
                    }

                    var importService = context.RequestServices.GetRequiredService<IGeoservicesImportService>();
                    artifact = await importService.ScanSourceAsync(
                        new GeoservicesDiscoveryRequest
                        {
                            ServiceUrl = request.SourceUrl,
                            TimeoutSeconds = request.TimeoutSeconds ?? 30
                        },
                        cancellationToken).ConfigureAwait(false);
                    break;
                }
            }

            await Results.Json(artifact, ImportJsonContext.Default.MigrationSourceInventoryArtifact)
                .ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Failed to connect to source service.",
                StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Source scan timed out.",
                StatusCodes.Status504GatewayTimeout);
        }
    }

    private static bool TryNormalizeSourceKind(string? sourceKind, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(sourceKind))
        {
            return false;
        }

        normalized = sourceKind.Trim().ToLowerInvariant() switch
        {
            "geoserver" or "geoserver-rest" => "geoserver-rest",
            "geoservices" or "arcgis-geoservices-rest" => "arcgis-geoservices-rest",
            _ => string.Empty
        };

        return normalized.Length > 0;
    }
}

/// <summary>
/// API request model for migration source inventory scans.
/// </summary>
internal sealed record MigrationInventoryScanRequest
{
    /// <summary>
    /// Source kind identifier.
    /// </summary>
    public string? SourceKind { get; init; }

    /// <summary>
    /// Source URL to scan.
    /// </summary>
    public string? SourceUrl { get; init; }

    /// <summary>
    /// Optional username for source authentication.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Optional password for source authentication.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Optional scan timeout in seconds.
    /// </summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>
    /// Whether to include GeoServer style content when available.
    /// </summary>
    public bool? IncludeStyleContent { get; init; }
}
