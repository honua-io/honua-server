// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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

        group.MapGet("/autonomy/policies", (Delegate)HandleListAutonomyPolicies)
            .WithDisplayName("List Ops Autonomy Policies")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<OpsAutonomyPolicyListResponse>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/autonomy/policies/{rule}", HandleGetAutonomyPolicy)
            .WithDisplayName("Get Ops Autonomy Policy")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<OpsAutonomyPolicyResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPut("/autonomy/policies/{rule}", HandleSetAutonomyPolicy)
            .WithDisplayName("Set Ops Autonomy Policy")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }))
            .Produces<OpsAutonomyPolicyResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/autonomy/settings", (Delegate)HandleGetAutonomySettings)
            .WithDisplayName("Get Ops Autonomy Settings")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<OpsAutonomySettingsResponse>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPut("/autonomy/settings", HandleSetAutonomySettings)
            .WithDisplayName("Set Ops Autonomy Settings")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }))
            .Produces<OpsAutonomySettingsResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
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
            Findings = findings.Select(OpsFindingResponseMapper.Map).ToList(),
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

    private static async Task<IResult> HandleListAutonomyPolicies(HttpContext context)
    {
        var store = ResolveAutonomyStore(context);
        if (store is null)
        {
            return AutonomyStoreUnavailable();
        }

        var snapshots = await store.ListPoliciesAsync(context.RequestAborted).ConfigureAwait(false);
        var response = new OpsAutonomyPolicyListResponse
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Policies = snapshots.Select(MapPolicySnapshot).ToArray(),
        };

        return Results.Json(response, OpsObservabilityJsonContext.Default.OpsAutonomyPolicyListResponse);
    }

    private static async Task<IResult> HandleGetAutonomyPolicy(string rule, HttpContext context)
    {
        if (!TryValidateRule(rule, out var error))
        {
            return BadRequest(error);
        }

        var store = ResolveAutonomyStore(context);
        if (store is null)
        {
            return AutonomyStoreUnavailable();
        }

        var policy = await store.GetPolicyAsync(rule, context.RequestAborted).ConfigureAwait(false);
        if (policy is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status404NotFound,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                $"Autonomy policy for rule '{rule}' was not found.");
        }

        var snapshots = await store.ListPoliciesAsync(context.RequestAborted).ConfigureAwait(false);
        var snapshot = snapshots.FirstOrDefault(item => string.Equals(item.Policy.Rule, rule, StringComparison.Ordinal))
            ?? new OpsAutonomyPolicySnapshot
            {
                Policy = policy,
                TrackRecord = EmptyTrack(rule),
            };

        return Results.Json(MapPolicySnapshot(snapshot), OpsObservabilityJsonContext.Default.OpsAutonomyPolicyResponse);
    }

    private static async Task<IResult> HandleSetAutonomyPolicy(
        string rule,
        OpsAutonomyPolicyUpdateRequest? request,
        [FromServices] IOptionsMonitor<OpsAutonomyOptions> options,
        HttpContext context)
    {
        if (!TryValidateRule(rule, out var error))
        {
            return BadRequest(error);
        }

        if (request is null)
        {
            return BadRequest("A request body is required.");
        }

        if (!TryParseMode(request.Mode, out var mode, out error))
        {
            return BadRequest(error);
        }

        if (request.MaxAutoActionsPerWindow is <= 0)
        {
            return BadRequest("'maxAutoActionsPerWindow' must be greater than zero.");
        }

        if (request.WindowSeconds is <= 0)
        {
            return BadRequest("'windowSeconds' must be greater than zero.");
        }

        if (request.MaxBlastRadius is <= 0)
        {
            return BadRequest("'maxBlastRadius' must be greater than zero.");
        }

        var store = ResolveAutonomyStore(context);
        if (store is null)
        {
            return AutonomyStoreUnavailable();
        }

        var existing = await store.GetPolicyAsync(rule, context.RequestAborted).ConfigureAwait(false)
            ?? BuildDefaultPolicy(rule, options.CurrentValue);
        var updated = existing with
        {
            Rule = rule,
            Mode = mode ?? existing.Mode,
            MaxAutoActionsPerWindow = request.MaxAutoActionsPerWindow ?? existing.MaxAutoActionsPerWindow,
            Window = TimeSpan.FromSeconds(request.WindowSeconds ?? (int)Math.Ceiling(existing.Window.TotalSeconds)),
            MaxBlastRadius = request.MaxBlastRadius ?? existing.MaxBlastRadius,
        };

        var snapshot = await store
            .SetPolicyAsync(updated, context.GetUserIdentity(), request.Reason, context.RequestAborted)
            .ConfigureAwait(false);

        return Results.Json(MapPolicySnapshot(snapshot), OpsObservabilityJsonContext.Default.OpsAutonomyPolicyResponse);
    }

    private static async Task<IResult> HandleGetAutonomySettings(HttpContext context)
    {
        var store = ResolveAutonomyStore(context);
        if (store is null)
        {
            return AutonomyStoreUnavailable();
        }

        var settings = await store.GetSettingsAsync(context.RequestAborted).ConfigureAwait(false);
        return Results.Json(MapSettings(settings), OpsObservabilityJsonContext.Default.OpsAutonomySettingsResponse);
    }

    private static async Task<IResult> HandleSetAutonomySettings(
        OpsAutonomySettingsUpdateRequest? request,
        HttpContext context)
    {
        if (request is null)
        {
            return BadRequest("A request body is required.");
        }

        if (!request.KillSwitchEnabled.HasValue)
        {
            return BadRequest("'killSwitchEnabled' is required.");
        }

        var store = ResolveAutonomyStore(context);
        if (store is null)
        {
            return AutonomyStoreUnavailable();
        }

        var settings = await store
            .SetSettingsAsync(
                new OpsAutonomySettings { KillSwitchEnabled = request.KillSwitchEnabled.Value },
                context.GetUserIdentity(),
                request.Reason,
                context.RequestAborted)
            .ConfigureAwait(false);

        return Results.Json(MapSettings(settings), OpsObservabilityJsonContext.Default.OpsAutonomySettingsResponse);
    }

    private static IOpsAutonomyPolicyStore? ResolveAutonomyStore(HttpContext context)
        => context.RequestServices.GetService<IOpsAutonomyPolicyStore>();

    private static IResult AutonomyStoreUnavailable()
        => ProblemDetailsHelpers.CreateAdminProblem(
            StatusCodes.Status503ServiceUnavailable,
            ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
            "The durable control plane is unavailable; ops autonomy is inert and policy changes are read-only.");

    private static IResult BadRequest(string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(
            StatusCodes.Status400BadRequest,
            ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
            detail);

    private static bool TryValidateRule(string? rule, out string error)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            error = "Rule is required.";
            return false;
        }

        if (rule.Length > 128)
        {
            error = "Rule must not exceed 128 characters.";
            return false;
        }

        if (rule.Any(static ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.')))
        {
            error = "Rule may contain only letters, numbers, hyphen, underscore, or dot.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryParseMode(string? raw, out OpsAutonomyMode? mode, out string error)
    {
        mode = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = string.Empty;
            return true;
        }

        if (Enum.TryParse<OpsAutonomyMode>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            mode = parsed;
            error = string.Empty;
            return true;
        }

        error = "'mode' must be ProposeOnly or AutoApply.";
        return false;
    }

    private static OpsAutonomyPolicy BuildDefaultPolicy(string rule, OpsAutonomyOptions options)
    {
        var mode = OpsAutonomyMode.ProposeOnly;
        var maxActions = options.DefaultMaxAutoActionsPerWindow;
        var windowSeconds = options.DefaultWindowSeconds;
        var maxBlastRadius = options.DefaultMaxBlastRadius;

        if (options.Rules.TryGetValue(rule, out var ruleOptions))
        {
            if (Enum.TryParse<OpsAutonomyMode>(ruleOptions.Mode, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            {
                mode = parsed;
            }

            maxActions = ruleOptions.MaxAutoActionsPerWindow ?? maxActions;
            windowSeconds = ruleOptions.WindowSeconds ?? windowSeconds;
            maxBlastRadius = ruleOptions.MaxBlastRadius ?? maxBlastRadius;
        }

        return new OpsAutonomyPolicy
        {
            Rule = rule,
            Mode = mode,
            MaxAutoActionsPerWindow = Math.Max(1, maxActions),
            Window = TimeSpan.FromSeconds(Math.Max(1, windowSeconds)),
            MaxBlastRadius = Math.Max(1, maxBlastRadius),
        };
    }

    private static OpsAutonomyPolicyResponse MapPolicySnapshot(OpsAutonomyPolicySnapshot snapshot)
        => new()
        {
            Rule = snapshot.Policy.Rule,
            Mode = snapshot.Policy.Mode.ToString(),
            MaxAutoActionsPerWindow = snapshot.Policy.MaxAutoActionsPerWindow,
            WindowSeconds = (int)Math.Ceiling(snapshot.Policy.Window.TotalSeconds),
            MaxBlastRadius = snapshot.Policy.MaxBlastRadius,
            UpdatedAt = snapshot.Policy.UpdatedAt,
            UpdatedBy = snapshot.Policy.UpdatedBy,
            TrackRecord = MapTrack(snapshot.TrackRecord),
        };

    private static OpsAutonomySettingsResponse MapSettings(OpsAutonomySettings settings)
        => new()
        {
            KillSwitchEnabled = settings.KillSwitchEnabled,
            UpdatedAt = settings.UpdatedAt,
            UpdatedBy = settings.UpdatedBy,
        };

    private static OpsAutonomyTrackRecordResponse MapTrack(OpsAutonomyTrackRecord track)
        => new()
        {
            ProposalsRaised = track.ProposalsRaised,
            ProposalsApproved = track.ProposalsApproved,
            ProposalsRejected = track.ProposalsRejected,
            AutoApplied = track.AutoApplied,
            RolledBack = track.RolledBack,
            Failed = track.Failed,
            FirstActivityAt = track.FirstActivityAt,
            LastActivityAt = track.LastActivityAt,
        };

    private static OpsAutonomyTrackRecord EmptyTrack(string rule)
        => new()
        {
            Rule = rule,
        };
}
