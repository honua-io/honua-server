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
/// OGC API Features collection import endpoints. Companion to the migration scanner endpoint:
/// once an operator confirms an inventory artifact, they invoke this endpoint to materialize a
/// single collection into the catalog target.
/// </summary>
internal static class OgcApiFeaturesImportEndpoints
{
    /// <summary>
    /// Maps OGC API Features collection import endpoints.
    /// </summary>
    public static void MapOgcApiFeaturesImportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/import/ogc-api-features")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Import")
            .RequireAdminAuthorization();

        _ = group.MapPost("/collection", HandleImportCollection)
            .WithName("ImportOgcApiFeaturesCollection")
            .WithSummary("Import an OGC API Features collection into the Honua catalog");
    }

    private static async Task HandleImportCollection(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        OgcApiFeaturesImportApiRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                ImportJsonContext.Default.OgcApiFeaturesImportApiRequest,
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
            await AdminResponseWriter.WriteErrorAsync(context, "ServiceUrl is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.CollectionId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "CollectionId is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.TargetSchema))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "TargetSchema is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (request.TimeoutSeconds is <= 0)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "TimeoutSeconds must be greater than 0.", StatusCodes.Status400BadRequest);
            return;
        }

        if (request.Filter != null && string.IsNullOrWhiteSpace(request.Filter))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Filter must be a non-empty CQL2-text expression when supplied.",
                StatusCodes.Status400BadRequest);
            return;
        }

        if (request.Bbox != null && request.Bbox.Length is not (4 or 6))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Bbox must contain exactly 4 (2D) or 6 (3D) ordinates.",
                StatusCodes.Status400BadRequest);
            return;
        }

        if (request.Datetime != null && string.IsNullOrWhiteSpace(request.Datetime))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Datetime must be a non-empty RFC3339 instant or interval when supplied.",
                StatusCodes.Status400BadRequest);
            return;
        }

        var allowUnsafeLocalUrls = GeoServerImportExecutionSettings.ShouldAllowUnsafeLocalUrls(context.RequestServices);
        var validation = await OgcServiceUrlValidation.ValidateAsync(
            request.ServiceUrl,
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

        var importService = context.RequestServices.GetRequiredService<IOgcApiFeaturesImportService>();
        var importRequest = new OgcApiFeaturesImportRequest
        {
            ServiceUrl = request.ServiceUrl,
            CollectionId = request.CollectionId,
            TargetSchema = request.TargetSchema,
            TargetTable = request.TargetTable,
            PageSize = request.PageSize ?? 500,
            MaxFeatures = request.MaxFeatures ?? 0,
            MaxPages = request.MaxPages ?? 1_000,
            TimeoutSeconds = request.TimeoutSeconds ?? 300,
            AllowUnsafeLocalUrls = allowUnsafeLocalUrls,
            Filter = request.Filter,
            Bbox = request.Bbox,
            Datetime = request.Datetime
        };

        var result = await importService.ImportCollectionAsync(importRequest, cancellationToken).ConfigureAwait(false);

        var statusCode = result.Success
            ? StatusCodes.Status200OK
            : result.ErrorCode switch
            {
                OgcApiFeaturesImportErrorCodes.InvalidServiceUrl => StatusCodes.Status400BadRequest,
                OgcApiFeaturesImportErrorCodes.InvalidFilter => StatusCodes.Status400BadRequest,
                OgcApiFeaturesImportErrorCodes.InvalidBbox => StatusCodes.Status400BadRequest,
                OgcApiFeaturesImportErrorCodes.InvalidDatetime => StatusCodes.Status400BadRequest,
                OgcApiFeaturesImportErrorCodes.UnsupportedItemsEncoding => StatusCodes.Status422UnprocessableEntity,
                OgcApiFeaturesImportErrorCodes.InvalidItemsDocument => StatusCodes.Status422UnprocessableEntity,
                OgcApiFeaturesImportErrorCodes.MissingItemsEndpoint => StatusCodes.Status422UnprocessableEntity,
                OgcApiFeaturesImportErrorCodes.SourceUnreachable => StatusCodes.Status502BadGateway,
                OgcApiFeaturesImportErrorCodes.Timeout => StatusCodes.Status504GatewayTimeout,
                OgcApiFeaturesImportErrorCodes.SinkFailure => StatusCodes.Status500InternalServerError,
                _ => StatusCodes.Status500InternalServerError
            };

        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(
            result,
            ImportJsonContext.Default.OgcApiFeaturesImportResult,
            contentType: null,
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// HTTP request body for the OGC API Features collection import endpoint.
/// </summary>
internal sealed record OgcApiFeaturesImportApiRequest
{
    /// <summary>Source OGC API Features landing page URL.</summary>
    public string? ServiceUrl { get; init; }

    /// <summary>Collection identifier to import.</summary>
    public string? CollectionId { get; init; }

    /// <summary>Target schema in the catalog.</summary>
    public string? TargetSchema { get; init; }

    /// <summary>Optional target table name. Defaults to <see cref="CollectionId"/> when omitted.</summary>
    public string? TargetTable { get; init; }

    /// <summary>Optional per-page size for items requests.</summary>
    public int? PageSize { get; init; }

    /// <summary>Optional cap on total features imported.</summary>
    public int? MaxFeatures { get; init; }

    /// <summary>Optional cap on the number of paged HTTP requests.</summary>
    public int? MaxPages { get; init; }

    /// <summary>Optional overall import timeout in seconds.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>Optional CQL2-text filter forwarded as <c>filter=...&amp;filter-lang=cql2-text</c>.</summary>
    public string? Filter { get; init; }

    /// <summary>Optional 4- or 6-tuple bbox forwarded as <c>bbox=</c>.</summary>
    public double[]? Bbox { get; init; }

    /// <summary>Optional RFC3339 instant or interval forwarded as <c>datetime=</c>.</summary>
    public string? Datetime { get; init; }
}
