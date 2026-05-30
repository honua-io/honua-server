// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
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
/// Admin endpoints for OGC Web Feature Service (WFS) data import.
/// </summary>
internal static class OgcWfsImportEndpoints
{
    /// <summary>
    /// Maps WFS data import endpoints.
    /// </summary>
    public static void MapOgcWfsImportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/import/ogc-wfs")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("OgcWfsImport")
            .RequireAdminAuthorization();

        _ = group.MapPost("/start", HandleStartImport)
            .WithName("StartOgcWfsImport")
            .WithSummary("Page features from a WFS source into Honua PostGIS and produce an initial migration manifest");
    }

    private static async Task HandleStartImport(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        OgcWfsImportApiRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                OgcWfsImportJsonContext.Default.OgcWfsImportApiRequest,
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
            await AdminResponseWriter.WriteErrorAsync(context, "serviceUrl is required.", StatusCodes.Status400BadRequest);
            return;
        }

        var isDryRun = request.DryRun ?? false;
        var isApplyMode = request.ApplyMode ?? false;
        if (!isDryRun && !isApplyMode)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Non-dry-run WFS imports require applyMode=true to acknowledge that the reviewed migration manifest will copy features into Honua PostGIS.",
                StatusCodes.Status400BadRequest);
            return;
        }

        var pageSize = request.PageSize ?? 1000;
        if (pageSize <= 0)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "pageSize must be greater than zero.", StatusCodes.Status400BadRequest);
            return;
        }

        var timeoutSeconds = request.RequestTimeoutSeconds ?? 120;
        if (timeoutSeconds <= 0)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "requestTimeoutSeconds must be greater than zero.", StatusCodes.Status400BadRequest);
            return;
        }

        var allowUnsafeLocalUrls = GeoServerImportExecutionSettings.ShouldAllowUnsafeLocalUrls(context.RequestServices);
        var validation = await OgcServiceUrlValidation.ValidateAsync(
            request.ServiceUrl,
            allowUnsafeLocalUrls,
            cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            await AdminResponseWriter.WriteErrorAsync(context, validation.ErrorMessage!, StatusCodes.Status400BadRequest);
            return;
        }

        var importService = context.RequestServices.GetRequiredService<IOgcWfsImportService>();

        OgcWfsImportResult result;
        try
        {
            result = await importService.ImportFeaturesAsync(
                new OgcWfsImportRequest
                {
                    ServiceUrl = request.ServiceUrl,
                    Version = request.Version,
                    Username = request.Username,
                    Password = request.Password,
                    FeatureTypeNames = request.FeatureTypeNames,
                    TargetSchema = request.TargetSchema,
                    OverwriteExisting = request.OverwriteExisting ?? false,
                    DryRun = isDryRun,
                    ApplyMode = isApplyMode,
                    TargetSrid = request.TargetSrid,
                    PageSize = pageSize,
                    RequestTimeoutSeconds = timeoutSeconds,
                    AllowUnsafeLocalUrls = allowUnsafeLocalUrls
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "WFS import was canceled by the operator.",
                StatusCodes.Status499ClientClosedRequest);
            return;
        }
        catch (ArgumentException ex)
        {
            await AdminResponseWriter.WriteErrorAsync(context, ex.Message, StatusCodes.Status400BadRequest);
            return;
        }

        if (!result.Success)
        {
            await Results.Json(result, OgcWfsImportJsonContext.Default.OgcWfsImportResult,
                    statusCode: StatusCodes.Status502BadGateway)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await Results.Json(result, OgcWfsImportJsonContext.Default.OgcWfsImportResult).ExecuteAsync(context).ConfigureAwait(false);
    }
}

/// <summary>
/// API request model for WFS data import.
/// </summary>
internal sealed record OgcWfsImportApiRequest
{
    /// <summary>WFS service URL (root or GetCapabilities URL).</summary>
    public string? ServiceUrl { get; init; }

    /// <summary>Optional WFS version such as 2.0.0, 1.1.0, or 1.0.0.</summary>
    public string? Version { get; init; }

    /// <summary>Optional HTTP Basic username for WFS authentication.</summary>
    public string? Username { get; init; }

    /// <summary>Optional HTTP Basic password for WFS authentication.</summary>
    public string? Password { get; init; }

    /// <summary>Specific feature types to import (omit to import all).</summary>
    public string[]? FeatureTypeNames { get; init; }

    /// <summary>Target PostGIS schema (defaults to the configured operational schema).</summary>
    public string? TargetSchema { get; init; }

    /// <summary>Whether to drop and recreate existing tables.</summary>
    public bool? OverwriteExisting { get; init; }

    /// <summary>Preview mode: build the manifest without writing to PostGIS.</summary>
    public bool? DryRun { get; init; }

    /// <summary>Operator opt-in safety gate for non-dry-run imports.</summary>
    public bool? ApplyMode { get; init; }

    /// <summary>Optional target SRID. Preserves source SRID when omitted.</summary>
    public int? TargetSrid { get; init; }

    /// <summary>Page size for paged GetFeature requests.</summary>
    public int? PageSize { get; init; }

    /// <summary>Per-WFS-call timeout in seconds.</summary>
    public int? RequestTimeoutSeconds { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OgcWfsImportApiRequest))]
[JsonSerializable(typeof(OgcWfsImportResult))]
[JsonSerializable(typeof(OgcWfsImportedFeatureType))]
[JsonSerializable(typeof(OgcWfsImportedFeatureType[]))]
[JsonSerializable(typeof(IReadOnlyList<OgcWfsImportedFeatureType>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class OgcWfsImportJsonContext : JsonSerializerContext
{
}
