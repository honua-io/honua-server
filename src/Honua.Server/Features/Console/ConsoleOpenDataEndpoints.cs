// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Console;

/// <summary>
/// Authenticated Console open-data publication endpoints: open-data page
/// read/write, eligibility evaluation, DCAT/data.json export preview with
/// validation status, and STAC publish/update/unpublish/status controls. All
/// routes require admin authorization — open-data publication is a privileged
/// management surface. These are thin adapters over the content/share/open-data
/// stores and the pure projection helpers in <see cref="ConsoleOpenDataMapper"/>.
/// </summary>
internal static class ConsoleOpenDataEndpoints
{
    /// <summary>Log category for the authenticated Console open-data endpoints.</summary>
    internal sealed class ConsoleOpenDataEndpointsLogCategory;

    /// <summary>Maps the authenticated Console open-data endpoints.</summary>
    public static void MapConsoleOpenDataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/console/content/{id}/open-data")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Console")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleGetPage)
            .WithDisplayName("Get Console Open-Data Page")
            .WithSummary("Returns the server-owned open-data page, eligibility decision, STAC publication state, and DCAT validation status for a content item.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPut("/", HandleSavePage)
            .WithDisplayName("Update Console Open-Data Page")
            .WithSummary("Creates or replaces the editable open-data page metadata for a content item.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapGet("/eligibility", HandleGetEligibility)
            .WithDisplayName("Evaluate Console Open-Data Eligibility")
            .WithSummary("Reports whether the item may be published as open data and the stable reason why or why not.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/dcat", HandleGetDcat)
            .WithDisplayName("Preview Console Open-Data DCAT Export")
            .WithSummary("Generates the DCAT-US 3.0/data.json export for the item's open-data page and reports its validation status.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/stac", HandleGetStacStatus)
            .WithDisplayName("Get Console Open-Data STAC Publication Status")
            .WithSummary("Returns the item's current STAC publication status and collection readback.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/stac/publish", HandlePublishStac)
            .WithDisplayName("Publish Console Open-Data to STAC")
            .WithSummary("Publishes (or re-publishes/updates) the item to the STAC catalog. Requires open-data eligibility and a DCAT-valid page.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapDelete("/stac", HandleUnpublishStac)
            .WithDisplayName("Unpublish Console Open-Data from STAC")
            .WithSummary("Unpublishes the item from the STAC catalog. Anonymous STAC reads return 404 afterward.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleOpenDataPageResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleGetPage(
        string id,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] IConsoleOpenDataStore openDataStore,
        [FromServices] ILogger<ConsoleOpenDataEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var item = await contentStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (item is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            var response = await BuildPageResponseAsync(item, shareStore, openDataStore, context.RequestAborted).ConfigureAwait(false);
            return TypedResults.Ok(ApiResponse<ConsoleOpenDataPageResponse>.CreateSuccess(response));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "open-data.get", ex);
            return InternalError("Console open-data page read failed", "An internal error occurred while reading the open-data page.");
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleOpenDataPageResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>> HandleSavePage(
        string id,
        UpdateOpenDataPageRequest request,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] IConsoleOpenDataStore openDataStore,
        [FromServices] ILogger<ConsoleOpenDataEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var item = await contentStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (item is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            if (request.Distributions is { } distributions)
            {
                foreach (var distribution in distributions)
                {
                    if (string.IsNullOrWhiteSpace(distribution.AccessUrl))
                    {
                        return TypedResults.BadRequest(ApiResponse<object>.Failure("Each distribution must have a non-empty accessUrl."));
                    }
                }
            }

            var principalId = ConsolePrincipal.ResolveActorId(context.User);
            var page = new ConsoleOpenDataPage
            {
                ItemId = id,
                Title = Trim(request.Title),
                Description = Trim(request.Description),
                PublisherName = Trim(request.PublisherName),
                ContactName = Trim(request.ContactName),
                ContactEmail = Trim(request.ContactEmail),
                License = Trim(request.License),
                LandingPage = Trim(request.LandingPage),
                Tags = request.Tags ?? Array.Empty<string>(),
                Distributions = request.Distributions ?? Array.Empty<ConsoleOpenDataDistribution>(),
                SpatialExtent = request.SpatialExtent,
                TemporalExtent = request.TemporalExtent,
                ProvenanceRefs = request.ProvenanceRefs ?? Array.Empty<string>(),
            };

            await openDataStore.SavePageAsync(page, principalId, context.RequestAborted).ConfigureAwait(false);
            ConsoleOpenDataEndpointsLog.PageSaved(logger, id, principalId);

            var response = await BuildPageResponseAsync(item, shareStore, openDataStore, context.RequestAborted).ConfigureAwait(false);
            return TypedResults.Ok(ApiResponse<ConsoleOpenDataPageResponse>.CreateSuccess(response));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "open-data.save", ex);
            return InternalError("Console open-data page write failed", "An internal error occurred while saving the open-data page.");
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleOpenDataEligibilityResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleGetEligibility(
        string id,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] IConsoleOpenDataStore openDataStore,
        [FromServices] ILogger<ConsoleOpenDataEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var item = await contentStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (item is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            var tier = await ResolveTierAsync(item, shareStore, context.RequestAborted).ConfigureAwait(false);
            var page = await openDataStore.GetPageAsync(id, context.RequestAborted).ConfigureAwait(false);
            var eligibility = ConsoleOpenDataMapper.EvaluateEligibility(item, tier, page is not null);
            return TypedResults.Ok(ApiResponse<ConsoleOpenDataEligibilityResponse>.CreateSuccess(eligibility));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "open-data.eligibility", ex);
            return InternalError("Console open-data eligibility failed", "An internal error occurred while evaluating open-data eligibility.");
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleDcatExportResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleGetDcat(
        string id,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleOpenDataStore openDataStore,
        [FromServices] ILogger<ConsoleOpenDataEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var item = await contentStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (item is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            var stored = await openDataStore.GetPageAsync(id, context.RequestAborted).ConfigureAwait(false);
            var page = ConsoleOpenDataMapper.BuildPageProjection(item, stored);
            var validation = ConsoleOpenDataMapper.ValidateForDcat(page);
            var catalog = ConsoleOpenDataMapper.BuildDcatCatalog(page);
            return TypedResults.Ok(ApiResponse<ConsoleDcatExportResponse>.CreateSuccess(new ConsoleDcatExportResponse
            {
                Catalog = catalog,
                Validation = validation,
            }));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "open-data.dcat", ex);
            return InternalError("Console open-data DCAT preview failed", "An internal error occurred while generating the DCAT export.");
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleStacPublicationState>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleGetStacStatus(
        string id,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleOpenDataStore openDataStore,
        [FromServices] ILogger<ConsoleOpenDataEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var item = await contentStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (item is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            var state = await openDataStore.GetStacPublicationAsync(id, context.RequestAborted).ConfigureAwait(false);
            return TypedResults.Ok(ApiResponse<ConsoleStacPublicationState>.CreateSuccess(state));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "open-data.stac.status", ex);
            return InternalError("Console STAC status read failed", "An internal error occurred while reading the STAC publication status.");
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleStacPublicationState>>, NotFound<ApiResponse<object>>, Conflict<ApiResponse<ConsoleOpenDataValidationResult>>, ProblemHttpResult>> HandlePublishStac(
        string id,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] IConsoleOpenDataStore openDataStore,
        [FromServices] ILogger<ConsoleOpenDataEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var item = await contentStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (item is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            var tier = await ResolveTierAsync(item, shareStore, context.RequestAborted).ConfigureAwait(false);
            var stored = await openDataStore.GetPageAsync(id, context.RequestAborted).ConfigureAwait(false);
            var eligibility = ConsoleOpenDataMapper.EvaluateEligibility(item, tier, stored is not null);
            if (!eligibility.Eligible)
            {
                ConsoleOpenDataEndpointsLog.StacPublishDenied(logger, id, eligibility.ReasonCode);
                return TypedResults.Conflict(ApiResponse<ConsoleOpenDataValidationResult>.Failure(
                    eligibility.Reason,
                    new ConsoleOpenDataValidationResult
                    {
                        IsValid = false,
                        Issues = new[]
                        {
                            new ConsoleOpenDataValidationIssue
                            {
                                Field = "eligibility",
                                Severity = ConsoleOpenDataValidationSeverity.Error,
                                Message = eligibility.Reason,
                            },
                        },
                    }));
            }

            // Publishing requires a DCAT-valid page so the catalog projection is
            // standards-conformant; documented validation errors block the write.
            var page = ConsoleOpenDataMapper.BuildPageProjection(item, stored);
            var validation = ConsoleOpenDataMapper.ValidateForDcat(page);
            if (!validation.IsValid)
            {
                ConsoleOpenDataEndpointsLog.StacPublishDenied(logger, id, "dcat-invalid");
                return TypedResults.Conflict(ApiResponse<ConsoleOpenDataValidationResult>.Failure(
                    "Open-data page has DCAT validation errors; resolve them before publishing.",
                    validation));
            }

            var principalId = ConsolePrincipal.ResolveActorId(context.User);
            var state = await openDataStore.PublishStacAsync(id, principalId, context.RequestAborted).ConfigureAwait(false);
            ConsoleOpenDataEndpointsLog.StacPublished(logger, id, state.CollectionId, state.Revision, principalId);
            return TypedResults.Ok(ApiResponse<ConsoleStacPublicationState>.CreateSuccess(state));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "open-data.stac.publish", ex);
            return InternalError("Console STAC publish failed", "An internal error occurred while publishing to the STAC catalog.");
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleStacPublicationState>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleUnpublishStac(
        string id,
        [FromServices] IConsoleOpenDataStore openDataStore,
        [FromServices] ILogger<ConsoleOpenDataEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var principalId = ConsolePrincipal.ResolveActorId(context.User);
            var state = await openDataStore.UnpublishStacAsync(id, principalId, context.RequestAborted).ConfigureAwait(false);
            if (state is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Item is not published to the STAC catalog."));

            ConsoleOpenDataEndpointsLog.StacUnpublished(logger, id, principalId);
            return TypedResults.Ok(ApiResponse<ConsoleStacPublicationState>.CreateSuccess(state));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "open-data.stac.unpublish", ex);
            return InternalError("Console STAC unpublish failed", "An internal error occurred while unpublishing from the STAC catalog.");
        }
    }

    private static async Task<ConsoleOpenDataPageResponse> BuildPageResponseAsync(
        ConsoleContentItem item,
        IConsoleShareStore shareStore,
        IConsoleOpenDataStore openDataStore,
        CancellationToken cancellationToken)
    {
        var tier = await ResolveTierAsync(item, shareStore, cancellationToken).ConfigureAwait(false);
        var stored = await openDataStore.GetPageAsync(item.Id, cancellationToken).ConfigureAwait(false);
        var publication = await openDataStore.GetStacPublicationAsync(item.Id, cancellationToken).ConfigureAwait(false);
        var page = ConsoleOpenDataMapper.BuildPageProjection(item, stored);
        var eligibility = ConsoleOpenDataMapper.EvaluateEligibility(item, tier, stored is not null);
        var validation = ConsoleOpenDataMapper.ValidateForDcat(page);
        return new ConsoleOpenDataPageResponse
        {
            Page = page,
            Eligibility = eligibility,
            StacPublication = publication,
            DcatValidation = validation,
        };
    }

    private static async Task<ConsoleShareAccessTier> ResolveTierAsync(
        ConsoleContentItem item,
        IConsoleShareStore shareStore,
        CancellationToken cancellationToken)
    {
        var state = await shareStore.GetAsync(item.Id, cancellationToken).ConfigureAwait(false);
        return ConsoleShareMapper.ResolveEffectiveTier(state, item);
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ProblemHttpResult InternalError(string title, string detail)
        => TypedResults.Problem(title: title, detail: detail, statusCode: StatusCodes.Status500InternalServerError);
}
