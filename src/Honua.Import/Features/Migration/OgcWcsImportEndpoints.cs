// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
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
/// API endpoints for legacy OGC Web Coverage Service (WCS 1.x / 2.x) coverage
/// imports. Issue #1030 slice 3 — the WCS counterpart to slice 2's
/// OGC API Coverages import surface.
/// </summary>
internal static class OgcWcsImportEndpoints
{
    /// <summary>
    /// Map OGC WCS import endpoints onto the web application with formal API versioning.
    /// </summary>
    public static void MapOgcWcsImportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/import/ogc-wcs")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("OgcWcsImport")
            .RequireAdminAuthorization();

        _ = group.MapPost("/import", HandleImportCoverages)
            .WithName("ImportOgcWcsCoverages")
            .WithSummary("Plan or apply a legacy WCS coverage import using a slice-1 migration inventory");
    }

    private static async Task HandleImportCoverages(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        OgcWcsImportApiRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                OgcWcsImportJsonContext.Default.OgcWcsImportApiRequest,
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

        // Safety gate: require explicit applyMode=true when dryRun=false. Matches the
        // slice-2 coverage endpoint contract so operators must acknowledge state mutation.
        var dryRun = request.DryRun ?? true;
        var applyMode = request.ApplyMode ?? false;
        if (!dryRun && !applyMode)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Non-dry-run WCS coverage imports require applyMode=true to acknowledge that coverage data will be written to the Honua raster store.",
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

        var importRequest = new OgcWcsImportRequest
        {
            ServiceUrl = request.ServiceUrl,
            Version = request.Version,
            Inventory = request.Inventory,
            OutputFormat = string.IsNullOrWhiteSpace(request.OutputFormat) ? "image/tiff" : request.OutputFormat!,
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
            var service = context.RequestServices.GetRequiredService<IOgcWcsImportService>();
            var result = await service.ImportAsync(importRequest, cancellationToken).ConfigureAwait(false);

            await Results.Json(result, OgcWcsImportJsonContext.Default.OgcWcsImportResult)
                .ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                ImportValidationErrorMessage.FromArgument(ex, "Invalid WCS import request."),
                StatusCodes.Status400BadRequest);
        }
        catch (HttpRequestException)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Failed to connect to WCS source service.",
                StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "WCS coverage import timed out.",
                StatusCodes.Status504GatewayTimeout);
        }
    }
}

/// <summary>
/// API request payload for legacy WCS coverage import operations.
/// </summary>
internal sealed record OgcWcsImportApiRequest
{
    /// <summary>
    /// Source WCS service URL.
    /// </summary>
    public string? ServiceUrl { get; init; }

    /// <summary>
    /// Optional WCS protocol version (e.g. <c>1.1.1</c>, <c>2.0.1</c>).
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Requested output format. Defaults to <c>image/tiff</c>; other formats are
    /// classified as manual-review.
    /// </summary>
    public string? OutputFormat { get; init; }

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
    /// Optional HTTP basic-auth username for protected WCS endpoints.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Optional HTTP basic-auth password for protected WCS endpoints.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Optional per-request timeout in seconds.
    /// </summary>
    public int? TimeoutSeconds { get; init; }
}
