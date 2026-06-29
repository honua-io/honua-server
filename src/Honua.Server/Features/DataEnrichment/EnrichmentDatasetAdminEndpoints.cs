// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.EnrichmentCatalog.Abstractions;
using Honua.Core.Features.EnrichmentCatalog.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.DataEnrichment.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.DataEnrichment;

/// <summary>
/// Admin registration surface for the managed enrichment-dataset catalog (#2280).
/// Designates an existing managed layer as a reusable enrichment dataset
/// (register / update / deregister), gated by the admin authorization policy.
/// Discovery (<c>GET /api/enrich/datasets</c>) is mapped separately and is
/// edition-filtered rather than admin-gated.
/// </summary>
/// <remarks>
/// No-op when <see cref="IEnrichmentDatasetCatalogStore"/> is not registered (the
/// registry table is Postgres-only); on non-Postgres profiles the catalog falls
/// back to configuration-driven datasets and the admin CRUD surface is suppressed.
/// </remarks>
internal static partial class EnrichmentDatasetAdminEndpoints
{
    private const string TagAdmin = "Admin";
    private const string TagEnrichment = "Data Enrichment";

    /// <summary>
    /// Maps the enrichment-dataset admin endpoints into the request pipeline.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapEnrichmentDatasetAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var serviceInspector = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (serviceInspector is not null && !serviceInspector.IsService(typeof(IEnrichmentDatasetCatalogStore)))
        {
            return endpoints;
        }

        var group = endpoints.MapGroup("/api/enrich/datasets")
            .WithTags(TagAdmin, TagEnrichment)
            .RequireAdminAuthorization();

        _ = group.MapPost("/", HandleRegister)
            .WithName("RegisterEnrichmentDataset")
            .WithSummary("Register a managed layer as an enrichment dataset.")
            .Accepts<RegisterEnrichmentDatasetRequest>("application/json")
            .Produces<EnrichmentDatasetMetadata>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        _ = group.MapPut("/{id}", HandleUpdate)
            .WithName("UpdateEnrichmentDataset")
            .WithSummary("Update a registered enrichment dataset.")
            .Accepts<UpdateEnrichmentDatasetRequest>("application/json")
            .Produces<EnrichmentDatasetMetadata>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        _ = group.MapDelete("/{id}", HandleDeregister)
            .WithName("DeregisterEnrichmentDataset")
            .WithSummary("Deregister an enrichment dataset.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> HandleRegister(
        HttpContext context,
        [FromBody] RegisterEnrichmentDatasetRequest request,
        [FromServices] IEnrichmentDatasetCatalogStore store,
        [FromServices] EnrichmentDatasetCatalogService catalog,
        [FromServices] ILogger<EnrichmentDatasetAdminLogCategory> logger,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "Request body is required.");
        }

        if (!TryBuildRecord(request, context, out var record, out var validationError))
        {
            EnrichmentDatasetAdminLog.ValidationFailed(logger, request.Id ?? "<unknown>", validationError);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, validationError);
        }

        try
        {
            var saved = await store.RegisterAsync(record, cancellationToken).ConfigureAwait(false);
            await catalog.InvalidateAsync(cancellationToken).ConfigureAwait(false);
            EnrichmentDatasetAdminLog.Registered(logger, saved.Id);
            return Results.Json(
                ToMetadata(saved),
                EnrichmentJsonContext.Default.EnrichmentDatasetMetadata,
                statusCode: StatusCodes.Status201Created);
        }
        catch (EnrichmentDatasetAlreadyExistsException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status409Conflict,
                "An enrichment dataset with this id is already registered.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EnrichmentDatasetAdminLog.OperationFailed(logger, "register", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to register enrichment dataset.");
        }
    }

    private static async Task<IResult> HandleUpdate(
        string id,
        HttpContext context,
        [FromBody] UpdateEnrichmentDatasetRequest request,
        [FromServices] IEnrichmentDatasetCatalogStore store,
        [FromServices] EnrichmentDatasetCatalogService catalog,
        [FromServices] ILogger<EnrichmentDatasetAdminLogCategory> logger,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "Request body is required.");
        }

        EnrichmentDatasetRecord? existing;
        try
        {
            existing = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EnrichmentDatasetAdminLog.OperationFailed(logger, "update.lookup", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to load enrichment dataset for update.");
        }

        if (existing is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                "Enrichment dataset not found.");
        }

        if (!TryApplyUpdate(existing, request, context, out var updated, out var validationError))
        {
            EnrichmentDatasetAdminLog.ValidationFailed(logger, existing.Id, validationError);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, validationError);
        }

        try
        {
            var saved = await store.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
            if (saved is null)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                    "Enrichment dataset not found.");
            }

            await catalog.InvalidateAsync(cancellationToken).ConfigureAwait(false);
            EnrichmentDatasetAdminLog.Updated(logger, saved.Id);
            return Results.Json(ToMetadata(saved), EnrichmentJsonContext.Default.EnrichmentDatasetMetadata);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EnrichmentDatasetAdminLog.OperationFailed(logger, "update", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to update enrichment dataset.");
        }
    }

    private static async Task<IResult> HandleDeregister(
        string id,
        HttpContext context,
        [FromServices] IEnrichmentDatasetCatalogStore store,
        [FromServices] EnrichmentDatasetCatalogService catalog,
        [FromServices] ILogger<EnrichmentDatasetAdminLogCategory> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
            if (!deleted)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                    "Enrichment dataset not found.");
            }

            await catalog.InvalidateAsync(cancellationToken).ConfigureAwait(false);
            EnrichmentDatasetAdminLog.Deregistered(logger, id);
            return Results.NoContent();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EnrichmentDatasetAdminLog.OperationFailed(logger, "deregister", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to deregister enrichment dataset.");
        }
    }

    private static bool TryBuildRecord(
        RegisterEnrichmentDatasetRequest request,
        HttpContext context,
        out EnrichmentDatasetRecord record,
        out string error)
    {
        record = null!;

        if (!EnrichmentDatasetValidation.TryValidateId(request.Id, out error)
            || !EnrichmentDatasetValidation.TryValidateTitle(request.Title, out error)
            || !EnrichmentDatasetValidation.TryValidateCategory(request.Category, out var category, out error)
            || !EnrichmentDatasetValidation.TryValidateLayerId(request.LayerId, out error)
            || !EnrichmentDatasetValidation.TryValidatePredicate(request.DefaultPredicate, out var predicate, out error)
            || !EnrichmentDatasetValidation.TryValidateMinimumEdition(request.MinimumEdition, out var edition, out error))
        {
            return false;
        }

        var actor = ResolveActor(context);
        var now = DateTimeOffset.UtcNow;
        record = new EnrichmentDatasetRecord(
            Id: request.Id!.Trim(),
            Title: request.Title!.Trim(),
            Category: category,
            LayerId: request.LayerId!.Value,
            GeometryType: NormalizeOptional(request.GeometryType),
            JoinAttributes: NormalizeAttributes(request.Attributes),
            DefaultPredicate: predicate,
            DistanceMeters: request.DistanceMeters,
            Provenance: NormalizeOptional(request.Provenance),
            Attribution: NormalizeOptional(request.Attribution),
            License: NormalizeOptional(request.License),
            MinimumEdition: edition,
            CreatedAt: now,
            UpdatedAt: now,
            CreatedBy: actor,
            UpdatedBy: actor);
        error = string.Empty;
        return true;
    }

    private static bool TryApplyUpdate(
        EnrichmentDatasetRecord existing,
        UpdateEnrichmentDatasetRequest request,
        HttpContext context,
        out EnrichmentDatasetRecord updated,
        out string error)
    {
        updated = existing;

        var title = request.Title ?? existing.Title;
        if (!EnrichmentDatasetValidation.TryValidateTitle(title, out error))
        {
            return false;
        }

        var category = existing.Category;
        if (request.Category is not null
            && !EnrichmentDatasetValidation.TryValidateCategory(request.Category, out category, out error))
        {
            return false;
        }

        var layerId = request.LayerId ?? existing.LayerId;
        if (!EnrichmentDatasetValidation.TryValidateLayerId(layerId, out error))
        {
            return false;
        }

        var predicate = existing.DefaultPredicate;
        if (request.DefaultPredicate is not null
            && !EnrichmentDatasetValidation.TryValidatePredicate(request.DefaultPredicate, out predicate, out error))
        {
            return false;
        }

        var edition = existing.MinimumEdition;
        if (request.MinimumEdition is not null
            && !EnrichmentDatasetValidation.TryValidateMinimumEdition(request.MinimumEdition, out edition, out error))
        {
            return false;
        }

        updated = existing with
        {
            Title = title.Trim(),
            Category = category,
            LayerId = layerId,
            GeometryType = request.GeometryType is null ? existing.GeometryType : NormalizeOptional(request.GeometryType),
            JoinAttributes = request.Attributes is null ? existing.JoinAttributes : NormalizeAttributes(request.Attributes),
            DefaultPredicate = predicate,
            DistanceMeters = request.DistanceMeters ?? existing.DistanceMeters,
            Provenance = request.Provenance is null ? existing.Provenance : NormalizeOptional(request.Provenance),
            Attribution = request.Attribution is null ? existing.Attribution : NormalizeOptional(request.Attribution),
            License = request.License is null ? existing.License : NormalizeOptional(request.License),
            MinimumEdition = edition,
            UpdatedBy = ResolveActor(context),
        };
        error = string.Empty;
        return true;
    }

    private static EnrichmentDatasetMetadata ToMetadata(EnrichmentDatasetRecord record) => new()
    {
        Id = record.Id,
        Title = record.Title,
        Category = record.Category,
        LayerId = record.LayerId,
        GeometryType = record.GeometryType,
        Attributes = record.JoinAttributes.ToArray(),
        DefaultPredicate = record.DefaultPredicate,
        DistanceMeters = record.DistanceMeters,
        Provenance = record.Provenance,
        Attribution = record.Attribution,
        License = record.License,
        MinimumEdition = record.MinimumEdition.ToString(),
        Source = "managed",
    };

    private static string[] NormalizeAttributes(string[]? attributes)
        => attributes is null
            ? []
            : attributes
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim())
                .ToArray();

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ResolveActor(HttpContext context)
        => context.User.Identity?.Name is { Length: > 0 } name ? name : "admin";

    /// <summary>Log category marker for the enrichment-dataset admin endpoints.</summary>
    internal sealed class EnrichmentDatasetAdminLogCategory;

    /// <summary>Source-generated logger messages for the enrichment-dataset admin endpoints.</summary>
    internal static partial class EnrichmentDatasetAdminLog
    {
        [LoggerMessage(EventId = 4700, Level = LogLevel.Information,
            Message = "Registered enrichment dataset '{DatasetId}'")]
        public static partial void Registered(ILogger logger, string datasetId);

        [LoggerMessage(EventId = 4701, Level = LogLevel.Information,
            Message = "Updated enrichment dataset '{DatasetId}'")]
        public static partial void Updated(ILogger logger, string datasetId);

        [LoggerMessage(EventId = 4702, Level = LogLevel.Information,
            Message = "Deregistered enrichment dataset '{DatasetId}'")]
        public static partial void Deregistered(ILogger logger, string datasetId);

        [LoggerMessage(EventId = 4703, Level = LogLevel.Warning,
            Message = "Enrichment dataset validation failed for '{DatasetId}': {Error}")]
        public static partial void ValidationFailed(ILogger logger, string datasetId, string error);

        [LoggerMessage(EventId = 4704, Level = LogLevel.Error,
            Message = "Enrichment dataset admin operation '{Operation}' failed.")]
        public static partial void OperationFailed(ILogger logger, string operation, Exception exception);
    }
}
