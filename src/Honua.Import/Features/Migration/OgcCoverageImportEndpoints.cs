// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;

namespace Honua.Migration;

/// <summary>
/// API endpoints for OGC WCS / OGC API Coverages GeoTIFF and Cloud Optimized GeoTIFF
/// coverage imports (issue #1030 slice 2).
/// </summary>
internal static class OgcCoverageImportEndpoints
{
    /// <summary>
    /// Map OGC coverage import endpoints to the web application with formal API versioning.
    /// </summary>
    public static void MapOgcCoverageImportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/import/ogc/coverages")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("OgcCoverageImport")
            .RequireAdminAuthorization();

        _ = group.MapPost("/import", HandleImportCoverages)
            .WithName("ImportOgcCoverages")
            .WithSummary("Plan or apply a GeoTIFF/COG coverage import using a slice-1 migration inventory");
    }

    private static async Task HandleImportCoverages(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        OgcCoverageImportApiRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                OgcCoverageImportJsonContext.Default.OgcCoverageImportApiRequest,
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

        if (string.IsNullOrWhiteSpace(request.ServiceType))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "ServiceType is required.", StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.ServiceUrl))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "ServiceUrl is required.", StatusCodes.Status400BadRequest);
            return;
        }

        if (request.Inventory == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Inventory is required.", StatusCodes.Status400BadRequest);
            return;
        }

        // Safety gate mirrors GeoServer import: explicit applyMode=true required to mutate state.
        var dryRun = request.DryRun ?? true;
        var applyMode = request.ApplyMode ?? false;
        if (!dryRun && !applyMode)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Non-dry-run OGC coverage imports require applyMode=true to acknowledge that GeoTIFF/COG coverage data will be written to the Honua raster store.",
                StatusCodes.Status400BadRequest);
            return;
        }

        var allowUnsafeLocalUrls = GeoServerImportExecutionSettings.ShouldAllowUnsafeLocalUrls(context.RequestServices);
        var urlValidation = await OgcServiceUrlValidation.ValidateAsync(
            request.ServiceUrl,
            allowUnsafeLocalUrls,
            cancellationToken).ConfigureAwait(false);
        if (!urlValidation.IsValid)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                urlValidation.ErrorMessage!,
                StatusCodes.Status400BadRequest);
            return;
        }

        var importRequest = new OgcCoverageImportRequest
        {
            ServiceType = request.ServiceType,
            ServiceUrl = request.ServiceUrl,
            Version = request.Version,
            Inventory = request.Inventory,
            CoverageSelection = request.CoverageSelection ?? [],
            Targets = request.Targets ?? new Dictionary<string, OgcCoverageImportTarget>(StringComparer.Ordinal),
            ApplyMode = applyMode,
            DryRun = dryRun,
            TargetServiceName = request.TargetServiceName,
            Username = request.Username,
            Password = request.Password,
            TimeoutSeconds = request.TimeoutSeconds ?? 120,
            AllowUnsafeLocalUrls = allowUnsafeLocalUrls
        };

        try
        {
            var service = context.RequestServices.GetRequiredService<IOgcCoverageImportService>();
            var result = await service.ImportAsync(importRequest, cancellationToken).ConfigureAwait(false);

            await Results.Json(result, OgcCoverageImportJsonContext.Default.OgcCoverageImportResult)
                .ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await AdminResponseWriter.WriteErrorAsync(context, ex.Message, StatusCodes.Status400BadRequest);
        }
        catch (HttpRequestException)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Failed to connect to coverage source service.",
                StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Coverage import timed out.",
                StatusCodes.Status504GatewayTimeout);
        }
    }
}

/// <summary>
/// API request payload for OGC coverage import operations.
/// </summary>
internal sealed record OgcCoverageImportApiRequest
{
    /// <summary>
    /// Coverage protocol surface. Accepted values are <c>WCS</c> and <c>OGC API Coverages</c>.
    /// </summary>
    public string? ServiceType { get; init; }

    /// <summary>
    /// Source coverage service URL.
    /// </summary>
    public string? ServiceUrl { get; init; }

    /// <summary>
    /// Optional source service version.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Slice-1 migration inventory artifact identifying the coverages to plan or import.
    /// </summary>
    public MigrationSourceInventoryArtifact? Inventory { get; init; }

    /// <summary>
    /// Optional explicit coverage selection. When omitted every coverage in the inventory is processed.
    /// </summary>
    public string[]? CoverageSelection { get; init; }

    /// <summary>
    /// Optional per-coverage import targets keyed by source coverage name or resource id.
    /// </summary>
    public IReadOnlyDictionary<string, OgcCoverageImportTarget>? Targets { get; init; }

    /// <summary>
    /// Optional dry-run preview flag. Defaults to <c>true</c>.
    /// </summary>
    public bool? DryRun { get; init; }

    /// <summary>
    /// Apply-mode opt-in. Required (alongside <c>dryRun=false</c>) to mutate raster store state.
    /// </summary>
    public bool? ApplyMode { get; init; }

    /// <summary>
    /// Optional target service name used when computing manifest identities.
    /// </summary>
    public string? TargetServiceName { get; init; }

    /// <summary>
    /// Optional HTTP basic-auth username for protected coverage endpoints.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Optional HTTP basic-auth password for protected coverage endpoints.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Optional per-request timeout in seconds.
    /// </summary>
    public int? TimeoutSeconds { get; init; }
}
