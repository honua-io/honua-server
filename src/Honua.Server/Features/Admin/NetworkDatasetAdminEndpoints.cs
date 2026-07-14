// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin editing surface for the addressable network-dataset registry (#1882). Provides
/// CRUD over <c>honua.network_datasets</c> — register / update / delete a dataset's
/// source-table mapping and metadata — gated by the admin authorization policy, mirroring
/// the scene-dataset registry admin endpoints.
/// </summary>
/// <remarks>
/// This edits the dataset <em>registry</em> (mapping + descriptive metadata), which is the
/// addressable editing surface built on the Phase-0 registry. Edge/vertex feature editing,
/// turn/restriction-attribute editing, and topology rebuild are deliberately deferred (the
/// rare, heavyweight tail of the parity row) and not exposed here.
/// All edge/vertex table names are validated as safe schema-qualified identifiers before
/// persistence because the routing read path interpolates them into the pgRouting
/// edges-SQL string.
/// </remarks>
internal static partial class NetworkDatasetAdminEndpoints
{
    private const string TagAdmin = "Admin";
    private const string TagNetworkDatasets = "Network Datasets";

    /// <summary>
    /// Maps the network-dataset admin endpoints into the request pipeline.
    /// </summary>
    /// <remarks>
    /// No-op when <see cref="INetworkDatasetStore"/> is not registered, which is the case
    /// for non-Postgres data-source profiles (the registry table is Postgres-only). The
    /// read-only routing path is unaffected; only the admin CRUD surface is suppressed.
    /// </remarks>
    public static IEndpointRouteBuilder MapNetworkDatasetAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var serviceInspector = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (serviceInspector is not null && !serviceInspector.IsService(typeof(INetworkDatasetStore)))
        {
            return endpoints;
        }

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/network-datasets")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags(TagAdmin, TagNetworkDatasets)
            .RequireAdminAuthorization();

        _ = group.MapGet("/", HandleList)
            .WithName("ListNetworkDatasets")
            .WithSummary("List registered network datasets.")
            .Produces<NetworkDatasetDto[]>(StatusCodes.Status200OK);

        _ = group.MapPost("/", HandleRegister)
            .WithName("RegisterNetworkDataset")
            .WithSummary("Register a new network dataset.")
            .Accepts<RegisterNetworkDatasetRequest>("application/json")
            .Produces<NetworkDatasetDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        _ = group.MapGet("/{id}", HandleGet)
            .WithName("GetNetworkDataset")
            .WithSummary("Get a network dataset by id.")
            .Produces<NetworkDatasetDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        _ = group.MapPut("/{id}", HandleUpdate)
            .WithName("UpdateNetworkDataset")
            .WithSummary("Update mutable fields of a network dataset.")
            .Accepts<UpdateNetworkDatasetRequest>("application/json")
            .Produces<NetworkDatasetDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        _ = group.MapDelete("/{id}", HandleDelete)
            .WithName("DeleteNetworkDataset")
            .WithSummary("Delete a network dataset.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> HandleList(
        HttpContext context,
        [FromServices] INetworkDatasetStore store,
        [FromServices] ILogger<NetworkDatasetAdminLogCategory> logger,
        [FromQuery] bool? includeDisabled,
        CancellationToken cancellationToken)
    {
        try
        {
            var records = await store.ListAsync(includeDisabled ?? false, cancellationToken).ConfigureAwait(false);
            var dtos = new NetworkDatasetDto[records.Count];
            for (var i = 0; i < records.Count; i++)
            {
                dtos[i] = ToDto(records[i]);
            }

            NetworkDatasetAdminLog.Listed(logger, dtos.Length);
            return Results.Json(dtos, NetworkDatasetAdminJsonContext.Default.NetworkDatasetDtoArray);
        }
        catch (Exception ex)
        {
            // Intentional catch-all request-handling boundary: logs and maps any failure
            // to a generic 500 admin problem response below.
            NetworkDatasetAdminLog.OperationFailed(logger, "list", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to list network datasets.");
        }
    }

    private static async Task<IResult> HandleGet(
        string id,
        HttpContext context,
        [FromServices] INetworkDatasetStore store,
        [FromServices] ILogger<NetworkDatasetAdminLogCategory> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var record = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                    "Network dataset not found.");
            }

            return Results.Json(ToDto(record), NetworkDatasetAdminJsonContext.Default.NetworkDatasetDto);
        }
        catch (Exception ex)
        {
            // Intentional catch-all request-handling boundary: logs and maps any failure
            // to a generic 500 admin problem response below.
            NetworkDatasetAdminLog.OperationFailed(logger, "get", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to retrieve network dataset.");
        }
    }

    private static async Task<IResult> HandleRegister(
        HttpContext context,
        [FromBody] RegisterNetworkDatasetRequest request,
        [FromServices] INetworkDatasetStore store,
        [FromServices] ILogger<NetworkDatasetAdminLogCategory> logger,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "Request body is required.");
        }

        if (!TryBuildRecord(request, context, out var record, out var validationError))
        {
            NetworkDatasetAdminLog.ValidationFailed(logger, request.Id ?? "<unknown>", validationError);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, validationError);
        }

        try
        {
            var saved = await store.RegisterAsync(record, cancellationToken).ConfigureAwait(false);
            NetworkDatasetAdminLog.Registered(logger, saved.Id);
            return Results.Json(
                ToDto(saved),
                NetworkDatasetAdminJsonContext.Default.NetworkDatasetDto,
                statusCode: StatusCodes.Status201Created);
        }
        catch (NetworkDatasetAlreadyExistsException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status409Conflict,
                "A network dataset with this id is already registered.");
        }
        catch (Exception ex)
        {
            // Intentional catch-all request-handling boundary: logs and maps any failure
            // to a generic 500 admin problem response below.
            NetworkDatasetAdminLog.OperationFailed(logger, "register", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to register network dataset.");
        }
    }

    private static async Task<IResult> HandleUpdate(
        string id,
        HttpContext context,
        [FromBody] UpdateNetworkDatasetRequest request,
        [FromServices] INetworkDatasetStore store,
        [FromServices] ILogger<NetworkDatasetAdminLogCategory> logger,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "Request body is required.");
        }

        NetworkDatasetRecord? existing;
        try
        {
            existing = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Intentional catch-all request-handling boundary: logs and maps any failure
            // to a generic 500 admin problem response below.
            NetworkDatasetAdminLog.OperationFailed(logger, "update.lookup", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to load network dataset for update.");
        }

        if (existing is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                "Network dataset not found.");
        }

        if (!TryApplyUpdate(existing, request, context, out var updated, out var validationError))
        {
            NetworkDatasetAdminLog.ValidationFailed(logger, existing.Id, validationError);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, validationError);
        }

        try
        {
            var saved = await store.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
            if (saved is null)
            {
                // Concurrently deleted between the lookup and the update.
                return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                    "Network dataset not found.");
            }

            NetworkDatasetAdminLog.Updated(logger, saved.Id);
            return Results.Json(ToDto(saved), NetworkDatasetAdminJsonContext.Default.NetworkDatasetDto);
        }
        catch (Exception ex)
        {
            // Intentional catch-all request-handling boundary: logs and maps any failure
            // to a generic 500 admin problem response below.
            NetworkDatasetAdminLog.OperationFailed(logger, "update", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to update network dataset.");
        }
    }

    private static async Task<IResult> HandleDelete(
        string id,
        HttpContext context,
        [FromServices] INetworkDatasetStore store,
        [FromServices] ILogger<NetworkDatasetAdminLogCategory> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
            if (!deleted)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                    "Network dataset not found.");
            }

            NetworkDatasetAdminLog.Deleted(logger, id);
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            // Intentional catch-all request-handling boundary: logs and maps any failure
            // to a generic 500 admin problem response below.
            NetworkDatasetAdminLog.OperationFailed(logger, "delete", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to delete network dataset.");
        }
    }

    private static bool TryBuildRecord(
        RegisterNetworkDatasetRequest request,
        HttpContext context,
        out NetworkDatasetRecord record,
        out string error)
    {
        record = null!;

        if (!NetworkDatasetValidation.TryValidateId(request.Id, out error))
        {
            return false;
        }

        if (!NetworkDatasetValidation.TryValidateName(request.Name, out error))
        {
            return false;
        }

        if (!NetworkDatasetValidation.TryValidateTableIdentifier(request.EdgeTable, "edgeTable", out error))
        {
            return false;
        }

        if (!NetworkDatasetValidation.TryValidateTableIdentifier(request.VertexTable, "vertexTable", out error))
        {
            return false;
        }

        var srid = request.Srid ?? 4326;
        if (!NetworkDatasetValidation.TryValidateSrid(srid, out error))
        {
            return false;
        }

        var status = string.IsNullOrWhiteSpace(request.Status) ? "active" : request.Status;
        if (!NetworkDatasetValidation.TryValidateStatus(status, out error))
        {
            return false;
        }

        if (!NetworkDatasetValidation.TryValidateDescription(request.Description, out error))
        {
            return false;
        }

        var actor = ResolveActor(context);
        var now = DateTimeOffset.UtcNow;
        record = new NetworkDatasetRecord(
            request.Id!,
            request.Name!,
            request.EdgeTable!,
            request.VertexTable!,
            srid,
            status,
            TopologyVersion: 1,
            NeedsRebuild: false,
            Description: NormalizeOptional(request.Description),
            CreatedAt: now,
            UpdatedAt: now,
            CreatedBy: actor,
            UpdatedBy: actor);
        error = string.Empty;
        return true;
    }

    private static bool TryApplyUpdate(
        NetworkDatasetRecord existing,
        UpdateNetworkDatasetRequest request,
        HttpContext context,
        out NetworkDatasetRecord updated,
        out string error)
    {
        updated = existing;

        var name = request.Name ?? existing.Name;
        if (!NetworkDatasetValidation.TryValidateName(name, out error))
        {
            return false;
        }

        var edgeTable = request.EdgeTable ?? existing.EdgeTable;
        if (!NetworkDatasetValidation.TryValidateTableIdentifier(edgeTable, "edgeTable", out error))
        {
            return false;
        }

        var vertexTable = request.VertexTable ?? existing.VertexTable;
        if (!NetworkDatasetValidation.TryValidateTableIdentifier(vertexTable, "vertexTable", out error))
        {
            return false;
        }

        var srid = request.Srid ?? existing.Srid;
        if (!NetworkDatasetValidation.TryValidateSrid(srid, out error))
        {
            return false;
        }

        var status = request.Status ?? existing.Status;
        if (!NetworkDatasetValidation.TryValidateStatus(status, out error))
        {
            return false;
        }

        var description = request.ClearDescription == true
            ? null
            : (request.Description ?? existing.Description);
        if (!NetworkDatasetValidation.TryValidateDescription(description, out error))
        {
            return false;
        }

        updated = existing with
        {
            Name = name,
            EdgeTable = edgeTable,
            VertexTable = vertexTable,
            Srid = srid,
            Status = status,
            Description = NormalizeOptional(description),
            UpdatedBy = ResolveActor(context),
        };
        error = string.Empty;
        return true;
    }

    private static NetworkDatasetDto ToDto(NetworkDatasetRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        EdgeTable = record.EdgeTable,
        VertexTable = record.VertexTable,
        Srid = record.Srid,
        Status = record.Status,
        TopologyVersion = record.TopologyVersion,
        NeedsRebuild = record.NeedsRebuild,
        Description = record.Description,
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        CreatedBy = record.CreatedBy,
        UpdatedBy = record.UpdatedBy,
    };

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string ResolveActor(HttpContext context)
        => context.User.Identity?.Name is { Length: > 0 } name ? name : "admin";

    /// <summary>
    /// Log category marker used by <see cref="ILogger{TCategoryName}"/> bindings.
    /// </summary>
    internal sealed class NetworkDatasetAdminLogCategory;

    /// <summary>
    /// Source-generated logger messages for the network-dataset admin endpoints.
    /// </summary>
    internal static partial class NetworkDatasetAdminLog
    {
        [LoggerMessage(EventId = 4600, Level = LogLevel.Information,
            Message = "Registered network dataset '{DatasetId}'")]
        public static partial void Registered(ILogger logger, string datasetId);

        [LoggerMessage(EventId = 4601, Level = LogLevel.Information,
            Message = "Updated network dataset '{DatasetId}'")]
        public static partial void Updated(ILogger logger, string datasetId);

        [LoggerMessage(EventId = 4602, Level = LogLevel.Information,
            Message = "Deleted network dataset '{DatasetId}'")]
        public static partial void Deleted(ILogger logger, string datasetId);

        [LoggerMessage(EventId = 4603, Level = LogLevel.Warning,
            Message = "Network dataset validation failed for '{DatasetId}': {Error}")]
        public static partial void ValidationFailed(ILogger logger, string datasetId, string error);

        [LoggerMessage(EventId = 4604, Level = LogLevel.Debug,
            Message = "Listed {Count} network datasets")]
        public static partial void Listed(ILogger logger, int count);

        [LoggerMessage(EventId = 4605, Level = LogLevel.Error,
            Message = "Network dataset admin operation '{Operation}' failed.")]
        public static partial void OperationFailed(ILogger logger, string operation, Exception exception);
    }
}
