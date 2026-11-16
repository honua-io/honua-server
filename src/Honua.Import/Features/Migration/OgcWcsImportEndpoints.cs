// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
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

internal static partial class OgcWcsImportEndpoints
{
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
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: ReadFromJsonAsync can throw JsonException, NotSupportedException,
        // or IOException for malformed/unreadable request bodies; map all of them to a 400 response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.RequestDeserializationFailed(GetLogger(context), ex);
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
        var allowedHostSuffixes = context.RequestServices.GetAllowedMigrationServiceHostSuffixes();
        var urlValidation = await OgcServiceUrlValidation.ValidateAsync(
            request.ServiceUrl,
            allowUnsafeLocalUrls,
            allowedHostSuffixes,
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
            var service = context.RequestServices.GetRequiredService<OgcWcsImportService>();
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

    private static ILogger<OgcWcsImportEndpointsLog> GetLogger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILogger<OgcWcsImportEndpointsLog>>();

    internal sealed class OgcWcsImportEndpointsLog;

    private static partial class Log
    {
        [LoggerMessage(8110, LogLevel.Warning, "Failed to deserialize WCS import request")]
        public static partial void RequestDeserializationFailed(ILogger logger, Exception exception);
    }
}

internal sealed record OgcWcsImportApiRequest
{
    public string? ServiceUrl { get; init; }

    public string? Version { get; init; }

    public string? OutputFormat { get; init; }

    public MigrationSourceInventoryArtifact? Inventory { get; init; }

    public string[]? CoverageSelection { get; init; }

    public IReadOnlyDictionary<string, OgcCoverageImportTarget>? Targets { get; init; }

    public bool? DryRun { get; init; }

    public bool? ApplyMode { get; init; }

    public string? TargetServiceName { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public int? TimeoutSeconds { get; init; }
}
