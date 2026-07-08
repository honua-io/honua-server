// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for the consolidated ops-health snapshot and the deterministic ops-findings
/// engine (ADR-0060 WS4 / epic #2457). Findings' recommended fixes are proposed through the existing
/// operation-gateway approval flow; no model calls happen server-side (ADR-0028).
/// </summary>
internal static class OpsObservabilityEndpoints
{
    /// <summary>
    /// Maps the admin observability endpoints (ops-health snapshot + ops findings).
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    public static void MapOpsObservabilityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Read-only ops-reader authorization (A12): the GET reads (ops-health, findings) additionally
        // admit an ops:read credential, while the mutating POST (findings/propose) still requires full
        // admin write — the ops-read policy is method-aware, so applying it at the group keeps every
        // mutation admin-only.
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/observability")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Observability")
            .RequireOpsReadAuthorization();

        group.MapGet("/ops-health", HandleGetOpsHealth)
            .WithDisplayName("Get Ops Health Snapshot")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<OpsHealthSnapshotResponse>();

        group.MapGet("/ops-health/history", HandleGetOpsHealthHistory)
            .WithDisplayName("Get Ops Health History")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<OpsHealthHistoryResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/findings", HandleGetFindings)
            .WithDisplayName("Get Ops Findings")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<OpsFindingsListResponse>();

        group.MapPost("/findings/{findingId}/propose", HandleProposeFinding)
            .WithDisplayName("Propose Ops Finding Action")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<OpsFindingProposeResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> HandleGetOpsHealth(
        [FromServices] IOpsHealthSnapshotService service,
        HttpContext context)
    {
        var snapshot = await service.GetAsync(context.RequestAborted).ConfigureAwait(false);
        return Results.Json(snapshot, OpsObservabilityJsonContext.Default.OpsHealthSnapshotResponse);
    }

    /// <summary>
    /// Handles the ops-health history read: an admin-authed, cluster-aggregated time series of serving
    /// latency and ops vitals at the requested <c>resolution</c> over the requested <c>window</c>, with an
    /// optional per-replica breakdown (<c>perReplica=true</c>). This endpoint is the reconnect gap-fill
    /// contract for realtime <c>ops-health</c> hub clients (#2554) — clients backfill a dropped interval by
    /// requesting the window rather than replaying per-event cursors.
    /// </summary>
    private static async Task<IResult> HandleGetOpsHealthHistory(
        [FromServices] IOpsHealthHistoryService service,
        HttpContext context,
        string? window = null,
        string? resolution = null,
        bool perReplica = false)
    {
        if (!OpsHealthHistoryQuery.TryParse(window, resolution, perReplica, out var error, out var query))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                error ?? "Invalid history query.");
        }

        var response = await service.GetAsync(query, context.RequestAborted).ConfigureAwait(false);
        return Results.Json(response, OpsObservabilityJsonContext.Default.OpsHealthHistoryResponse);
    }

    private static async Task<IResult> HandleGetFindings(
        [FromServices] IOpsFindingsService service,
        HttpContext context)
    {
        var findings = await service.EvaluateAsync(context.RequestAborted).ConfigureAwait(false);
        var response = new OpsFindingsListResponse
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Findings = findings.Select(MapFinding).ToList(),
        };

        return Results.Json(response, OpsObservabilityJsonContext.Default.OpsFindingsListResponse);
    }

    private static async Task<IResult> HandleProposeFinding(
        string findingId,
        [FromServices] IOpsFindingsService service,
        HttpContext context)
    {
        var result = await service.ProposeAsync(findingId, context.RequestAborted).ConfigureAwait(false);

        if (result.Status is OpsFindingProposalStatus.FindingNotFound or OpsFindingProposalStatus.NoRecommendedAction)
        {
            var detail = result.Status == OpsFindingProposalStatus.FindingNotFound
                ? $"Finding '{findingId}' was not found or its underlying condition has cleared."
                : $"Finding '{findingId}' has no recommended action to propose.";
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status404NotFound,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                detail);
        }

        if (result.Status is OpsFindingProposalStatus.GatewayUnavailable)
        {
            // The server is running without its durable control-plane backend, so the approval
            // gateway is not wired and the recommended fix cannot be routed (#2511). Surface a 503
            // so callers can distinguish "temporarily degraded" from a not-found/no-action finding.
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status503ServiceUnavailable,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                result.Message
                    ?? "The control-plane operation gateway is unavailable; the recommended action cannot be proposed.");
        }

        var response = new OpsFindingProposeResponse
        {
            FindingId = result.FindingId,
            Status = result.Status.ToString(),
            ProposalId = result.ProposalId,
            ExecutionOperationId = result.ExecutionOperationId,
            Message = result.Message,
        };

        return Results.Json(response, OpsObservabilityJsonContext.Default.OpsFindingProposeResponse);
    }

    private static OpsFindingView MapFinding(OpsFinding finding)
        => new()
        {
            Id = finding.Id,
            Rule = finding.Rule,
            Severity = finding.Severity.ToString(),
            Title = finding.Title,
            Explanation = finding.Explanation,
            DetectedAt = finding.DetectedAt,
            Subject = new OpsFindingSubjectView
            {
                TargetId = finding.Subject.TargetId,
                WorkloadId = finding.Subject.WorkloadId,
                Channel = finding.Subject.Channel,
                OperationId = finding.Subject.OperationId,
                ReleaseVersion = finding.Subject.ReleaseVersion,
                Protocol = finding.Subject.Protocol,
            },
            EvidenceRefs = finding.EvidenceRefs,
            RecommendedAction = finding.RecommendedAction is null
                ? null
                : new OpsFindingActionView
                {
                    Kind = finding.RecommendedAction.Kind.ToString(),
                    Summary = finding.RecommendedAction.Summary,
                    Reason = finding.RecommendedAction.Reason,
                },
        };
}
