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
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/observability")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Observability")
            .RequireAdminAuthorization();

        group.MapGet("/ops-health", HandleGetOpsHealth)
            .WithDisplayName("Get Ops Health Snapshot")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<OpsHealthSnapshotResponse>();

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
