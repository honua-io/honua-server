// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;

namespace Honua.Server.Features.Import;

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
            AllowUnsafeLocalUrls = allowUnsafeLocalUrls
        };

        var result = await importService.ImportCollectionAsync(importRequest, cancellationToken).ConfigureAwait(false);

        var statusCode = result.Success
            ? StatusCodes.Status200OK
            : result.ErrorCode switch
            {
                OgcApiFeaturesImportErrorCodes.InvalidServiceUrl => StatusCodes.Status400BadRequest,
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
}
