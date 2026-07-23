// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Admin.Routing;

/// <summary>
/// Admin submission and status surface for isolated shadow-topology rebuilds (#2718).
/// Submission creates a durable <c>NetworkTopologyRebuild</c> execution job; equivalent
/// generic status/cancel/retry operations are served by the shared
/// <c>/api/v1/admin/jobs/{operationId}</c> surface (<c>ConsoleJobEndpoints</c>) rather than
/// duplicated here. This surface additionally exposes rebuild-specific stage checkpoints
/// the generic job surface does not know about.
/// </summary>
internal static partial class NetworkTopologyRebuildAdminEndpoints
{
    private const string TagAdmin = "Admin";
    private const string TagNetworkDatasets = "Network Datasets";
    private const string IfMatchHeader = "If-Match";

    /// <summary>
    /// Maps the network-topology rebuild admin endpoints into the request pipeline.
    /// </summary>
    /// <remarks>
    /// No-op when <see cref="INetworkTopologyRebuildStore"/> is not registered
    /// (non-Postgres data providers), mirroring the other network-topology admin surfaces.
    /// </remarks>
    public static IEndpointRouteBuilder MapNetworkTopologyRebuildAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var serviceInspector = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (serviceInspector is not null && !serviceInspector.IsService(typeof(INetworkTopologyRebuildStore)))
        {
            return endpoints;
        }

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/network-datasets/{id}/generations/{generation:long}/rebuild")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags(TagAdmin, TagNetworkDatasets)
            .RequireAdminAuthorization();

        _ = group.MapPost("/", HandleSubmitRebuild)
            .WithName("SubmitNetworkTopologyRebuild")
            .WithSummary("Submit an isolated shadow-topology rebuild for a dirty generation.")
            .Produces<NetworkTopologyRebuildSubmissionDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        _ = group.MapGet("/{attempt:long}", HandleGetRebuildAttempt)
            .WithName("GetNetworkTopologyRebuildAttempt")
            .WithSummary("Get a rebuild attempt's status and stage checkpoints.")
            .Produces<NetworkTopologyRebuildAttemptDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> HandleSubmitRebuild(
        string id,
        long generation,
        HttpContext context,
        [FromServices] INetworkTopologyGenerationStore generationStore,
        [FromServices] NetworkTopologyRebuildSubmissionService submissionService,
        [FromServices] ILogger<NetworkTopologyRebuildAdminLogCategory> logger,
        CancellationToken cancellationToken)
    {
        if (!TryResolveExpectedRowVersion(context, out var expectedRowVersion, out var headerError))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, headerError);
        }

        NetworkTopologyGeneration? target;
        try
        {
            target = await generationStore.GetAsync(id, generation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NetworkTopologyRebuildAdminLog.OperationFailed(logger, "rebuild.lookup-generation", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to load the topology generation.");
        }

        if (target is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                "Topology generation not found.");
        }

        try
        {
            var result = await submissionService.SubmitAsync(
                    id, generation, expectedRowVersion, target.SourceRevision, target.Srid, cancellationToken)
                .ConfigureAwait(false);

            var actor = ResolveActor(context);
            NetworkTopologyRebuildAdminLog.RebuildSubmitted(logger, id, generation, result.Attempt, actor);
            return Results.Json(
                new NetworkTopologyRebuildSubmissionDto { OperationId = result.OperationId, Generation = result.Generation, Attempt = result.Attempt },
                NetworkTopologyRebuildAdminJsonContext.Default.NetworkTopologyRebuildSubmissionDto,
                statusCode: StatusCodes.Status202Accepted);
        }
        catch (NetworkTopologyRebuildConflictException ex)
        {
            var status = ex.Reason == NetworkTopologyRebuildRejection.GenerationNotFound
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status409Conflict;
            return ProblemDetailsHelpers.CreateAdminProblem(context, status, ex.Message);
        }
        catch (NetworkTopologyRebuildUnavailableException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NetworkTopologyRebuildAdminLog.OperationFailed(logger, "rebuild.submit", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to submit the topology rebuild.");
        }
    }

    private static async Task<IResult> HandleGetRebuildAttempt(
        string id,
        long generation,
        long attempt,
        HttpContext context,
        [FromServices] INetworkTopologyRebuildStore rebuildStore,
        [FromServices] ILogger<NetworkTopologyRebuildAdminLogCategory> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var record = await rebuildStore.GetAttemptAsync(id, generation, attempt, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                    "Rebuild attempt not found.");
            }

            var checkpoints = await rebuildStore.ListCheckpointsAsync(id, generation, attempt, cancellationToken).ConfigureAwait(false);
            return Results.Json(ToDto(record, checkpoints), NetworkTopologyRebuildAdminJsonContext.Default.NetworkTopologyRebuildAttemptDto);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NetworkTopologyRebuildAdminLog.OperationFailed(logger, "rebuild.get-attempt", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to load the rebuild attempt.");
        }
    }

    private static bool TryResolveExpectedRowVersion(HttpContext context, out long rowVersion, out string error)
    {
        rowVersion = 0;
        error = string.Empty;

        if (!context.Request.Headers.TryGetValue(IfMatchHeader, out var values) || values.Count == 0)
        {
            error = $"{IfMatchHeader} header is required and must carry the generation's current row version.";
            return false;
        }

        var raw = values[0]?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(raw) || !long.TryParse(raw, out rowVersion) || rowVersion <= 0)
        {
            error = $"{IfMatchHeader} header must be a positive integer row version.";
            return false;
        }

        return true;
    }

    private static NetworkTopologyRebuildAttemptDto ToDto(
        NetworkTopologyRebuildAttempt attempt, IReadOnlyList<NetworkTopologyRebuildCheckpoint> checkpoints) => new()
        {
            DatasetId = attempt.DatasetId,
            Generation = attempt.Generation,
            Attempt = attempt.Attempt,
            State = FormatState(attempt.State),
            OperationId = attempt.OperationId,
            FailureCode = attempt.FailureCode,
            EvidenceDigest = attempt.EvidenceDigest,
            OwnerId = attempt.OwnerId,
            LeaseExpiresAt = attempt.LeaseExpiresAt,
            LastHeartbeatAt = attempt.LastHeartbeatAt,
            CreatedAt = attempt.CreatedAt,
            UpdatedAt = attempt.UpdatedAt,
            CompletedAt = attempt.CompletedAt,
            Checkpoints = checkpoints.Select(ToDto).ToArray(),
        };

    private static NetworkTopologyRebuildCheckpointDto ToDto(NetworkTopologyRebuildCheckpoint checkpoint) => new()
    {
        Stage = FormatStage(checkpoint.Stage),
        Status = FormatCheckpointStatus(checkpoint.Status),
        Detail = checkpoint.Detail,
        UpdatedAt = checkpoint.UpdatedAt,
    };

    private static string FormatState(NetworkTopologyRebuildAttemptState state) => state switch
    {
        NetworkTopologyRebuildAttemptState.Building => "building",
        NetworkTopologyRebuildAttemptState.Ready => "ready",
        NetworkTopologyRebuildAttemptState.Failed => "failed",
        _ => "unknown",
    };

    private static string FormatStage(NetworkTopologyRebuildStage stage) => stage switch
    {
        NetworkTopologyRebuildStage.Snapshot => "snapshot",
        NetworkTopologyRebuildStage.Build => "build",
        NetworkTopologyRebuildStage.Analyze => "analyze",
        NetworkTopologyRebuildStage.Validate => "validate",
        NetworkTopologyRebuildStage.Cleanup => "cleanup",
        _ => "unknown",
    };

    private static string FormatCheckpointStatus(NetworkTopologyRebuildCheckpointStatus status) => status switch
    {
        NetworkTopologyRebuildCheckpointStatus.Pending => "pending",
        NetworkTopologyRebuildCheckpointStatus.InProgress => "in_progress",
        NetworkTopologyRebuildCheckpointStatus.Completed => "completed",
        NetworkTopologyRebuildCheckpointStatus.Failed => "failed",
        _ => "unknown",
    };

    private static string ResolveActor(HttpContext context)
        => context.User.Identity?.Name is { Length: > 0 } name ? name : "admin";

    /// <summary>
    /// Log category marker used by <see cref="ILogger{TCategoryName}"/> bindings.
    /// </summary>
    internal sealed class NetworkTopologyRebuildAdminLogCategory;

    /// <summary>
    /// Source-generated logger messages for the network-topology rebuild admin endpoints.
    /// Structured audit fields exclude geometry, attribute values, and profile costs.
    /// </summary>
    internal static partial class NetworkTopologyRebuildAdminLog
    {
        [LoggerMessage(EventId = 4800, Level = LogLevel.Information,
            Message = "Submitted topology rebuild for dataset '{DatasetId}' generation {Generation} attempt {Attempt}: actor={Actor}")]
        public static partial void RebuildSubmitted(ILogger logger, string datasetId, long generation, long attempt, string actor);

        [LoggerMessage(EventId = 4801, Level = LogLevel.Error,
            Message = "Network topology rebuild admin operation '{Operation}' failed.")]
        public static partial void OperationFailed(ILogger logger, string operation, Exception exception);
    }
}
