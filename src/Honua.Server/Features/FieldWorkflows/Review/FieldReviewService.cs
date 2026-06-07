// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FieldWorkflows.Review;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.FieldWorkflows.Review;

/// <summary>
/// Back-office field data review and QA service (#1159). Adapts admin requests
/// into the shared <see cref="IFieldReviewStore"/>, audits every review state
/// transition, and maps failures through shared problem helpers. Authorization
/// is enforced at the route group via <c>RequireAdminAuthorization</c>.
/// </summary>
internal sealed class FieldReviewService
{
    private const int MaxLimit = 500;

    private readonly IFieldReviewStore _store;
    private readonly IAuditLog _auditLog;
    private readonly ILogger<FieldReviewService> _logger;

    public FieldReviewService(
        IFieldReviewStore store,
        IAuditLog auditLog,
        ILogger<FieldReviewService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IResult> ListAsync(HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("FieldReview.List");
        activity?.SetTag("honua.protocol", "field-workflows");
        activity?.SetTag("honua.operation", "list-submissions");

        var request = context.Request;
        if (!TryParseDateTime(request.Query["submittedFrom"], out var submittedFrom))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, "submittedFrom must be an ISO-8601 timestamp.");
        }

        if (!TryParseDateTime(request.Query["submittedTo"], out var submittedTo))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, "submittedTo must be an ISO-8601 timestamp.");
        }

        if (!TryParseInt(request.Query["layerId"], out var layerId))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, "layerId must be an integer.");
        }

        var reviewStatus = Trimmed(request.Query["reviewStatus"]);
        if (reviewStatus is not null && !FieldReviewStatus.IsValid(reviewStatus))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, $"reviewStatus must be one of: {string.Join(", ", FieldReviewStatus.All)}.");
        }

        var query = new FieldReviewQuery
        {
            FormId = Trimmed(request.Query["formId"]),
            ServiceId = Trimmed(request.Query["serviceId"]),
            LayerId = layerId,
            ReviewStatus = reviewStatus,
            AssignedTo = Trimmed(request.Query["assignedTo"]),
            SyncStatus = Trimmed(request.Query["syncStatus"]),
            HasConflict = TryParseBool(request.Query["hasConflict"]),
            SubmittedFrom = submittedFrom,
            SubmittedTo = submittedTo,
            Limit = TryParseInt(request.Query["limit"], out var limit) && limit is int l ? Math.Clamp(l, 1, MaxLimit) : 50,
            Offset = TryParseInt(request.Query["offset"], out var offset) && offset is int o ? Math.Max(0, o) : 0
        };

        var result = await _store.ListSubmissionsAsync(query, context.RequestAborted).ConfigureAwait(false);
        activity?.SetTag("honua.result_count", result.Items.Length);
        ApplyNoStore(context.Response);
        return Results.Json(result, FieldReviewJsonContext.Default.FieldSubmissionListResult);
    }

    public async Task<IResult> GetAsync(HttpContext context, Guid submissionId)
    {
        var detail = await _store.GetSubmissionAsync(submissionId, context.RequestAborted).ConfigureAwait(false);
        if (detail is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, $"Submission '{submissionId}' was not found.");
        }

        ApplyNoStore(context.Response);
        return Results.Json(detail, FieldReviewJsonContext.Default.FieldSubmissionDetail);
    }

    public async Task<IResult> AssignAsync(HttpContext context, Guid submissionId)
    {
        var request = await ReadAsync(context, FieldReviewJsonContext.Default.FieldReviewAssignmentRequest).ConfigureAwait(false);
        if (request is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, "Request body must be a valid assignment request.");
        }

        var actor = ResolveActor(context);
        var state = await _store.AssignAsync(submissionId, request.AssignedTo, actor, context.RequestAborted).ConfigureAwait(false);
        if (state is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, $"Submission '{submissionId}' was not found.");
        }

        await RecordAuditAsync(context, "field.review.assign", submissionId, AuditOutcome.Success,
            $"{{\"assignedTo\":{JsonString(request.AssignedTo)},\"status\":{JsonString(state.Status)}}}").ConfigureAwait(false);
        FieldReviewLog.Assigned(_logger, submissionId, request.AssignedTo ?? "<unassigned>");
        ApplyNoStore(context.Response);
        return Results.Json(state, FieldReviewJsonContext.Default.FieldReviewState);
    }

    public async Task<IResult> DecideAsync(HttpContext context, Guid submissionId)
    {
        var request = await ReadAsync(context, FieldReviewJsonContext.Default.FieldReviewDecisionRequest).ConfigureAwait(false);
        if (request is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, "Request body must be a valid decision request.");
        }

        if (!IsDecisionStatus(request.Status))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                $"status must be one of: {FieldReviewStatus.Approved}, {FieldReviewStatus.Rejected}, {FieldReviewStatus.ChangesRequested}.");
        }

        var expectedETag = context.Request.Headers["If-Match"].FirstOrDefault();
        var actor = ResolveActor(context);

        FieldReviewState? state;
        try
        {
            state = await _store.RecordDecisionAsync(submissionId, request.Status, request.Note, expectedETag, actor, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (FieldReviewConcurrencyException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status409Conflict, "The review state was modified concurrently; reload and retry.");
        }

        if (state is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, $"Submission '{submissionId}' was not found.");
        }

        await RecordAuditAsync(context, $"field.review.decision.{request.Status}", submissionId, AuditOutcome.Success,
            $"{{\"status\":{JsonString(request.Status)},\"hasNote\":{(string.IsNullOrWhiteSpace(request.Note) ? "false" : "true")}}}").ConfigureAwait(false);
        FieldReviewLog.Decided(_logger, submissionId, request.Status);
        context.Response.Headers.ETag = state.ETag;
        ApplyNoStore(context.Response);
        return Results.Json(state, FieldReviewJsonContext.Default.FieldReviewState);
    }

    public async Task<IResult> AddCommentAsync(HttpContext context, Guid submissionId)
    {
        var request = await ReadAsync(context, FieldReviewJsonContext.Default.FieldReviewCommentRequest).ConfigureAwait(false);
        if (request is null || string.IsNullOrWhiteSpace(request.Body))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, "Request body must include a non-empty comment 'body'.");
        }

        var actor = ResolveActor(context);
        var comment = await _store.AddCommentAsync(submissionId, request.Body, request.CorrectionRequest, actor, context.RequestAborted)
            .ConfigureAwait(false);
        if (comment is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, $"Submission '{submissionId}' was not found.");
        }

        var action = request.CorrectionRequest ? "field.review.correction_request" : "field.review.comment";
        await RecordAuditAsync(context, action, submissionId, AuditOutcome.Success,
            $"{{\"commentId\":{JsonString(comment.CommentId.ToString())},\"correctionRequest\":{(request.CorrectionRequest ? "true" : "false")}}}").ConfigureAwait(false);
        FieldReviewLog.Commented(_logger, submissionId, request.CorrectionRequest);
        ApplyNoStore(context.Response);
        return Results.Json(comment, FieldReviewJsonContext.Default.FieldReviewComment, statusCode: StatusCodes.Status201Created);
    }

    private static bool IsDecisionStatus(string? status)
        => string.Equals(status, FieldReviewStatus.Approved, StringComparison.Ordinal)
           || string.Equals(status, FieldReviewStatus.Rejected, StringComparison.Ordinal)
           || string.Equals(status, FieldReviewStatus.ChangesRequested, StringComparison.Ordinal);

    private static async Task<T?> ReadAsync<T>(HttpContext context, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(context.Request.Body, typeInfo, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryParseInt(string? value, out int? result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = null;
            return true;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            result = parsed;
            return true;
        }

        result = null;
        return false;
    }

    private static bool TryParseDateTime(string? value, out DateTimeOffset? result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = null;
            return true;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            result = parsed;
            return true;
        }

        result = null;
        return false;
    }

    private static bool? TryParseBool(string? value)
        => bool.TryParse(value, out var parsed) ? parsed : null;

    private static void ApplyNoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }

    private static string ResolveActor(HttpContext context)
        => context.User.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? context.User.Identity?.Name
           ?? AuditEvent.AnonymousActor;

    private static string JsonString(string? value)
        => value is null ? "null" : "\"" + JsonEncodedText.Encode(value) + "\"";

    private Task RecordAuditAsync(
        HttpContext context,
        string action,
        Guid submissionId,
        AuditOutcome outcome,
        string details)
        => _auditLog.RecordAsync(new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.AdminAction,
            Actor = ResolveActor(context),
            ActorType = context.User.Identity?.IsAuthenticated == true ? AuditActorType.UserId : AuditActorType.Anonymous,
            ResourceType = "field_submission",
            ResourceId = submissionId.ToString(),
            Action = action,
            Outcome = outcome,
            CorrelationId = context.TraceIdentifier,
            RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            Details = details
        }, context.RequestAborted);
}

internal static partial class FieldReviewLog
{
    [LoggerMessage(EventId = 118500, Level = LogLevel.Information, Message = "Assigned field submission {SubmissionId} to {Assignee}.")]
    public static partial void Assigned(ILogger logger, Guid submissionId, string assignee);

    [LoggerMessage(EventId = 118501, Level = LogLevel.Information, Message = "Recorded review decision {Status} for field submission {SubmissionId}.")]
    public static partial void Decided(ILogger logger, Guid submissionId, string status);

    [LoggerMessage(EventId = 118502, Level = LogLevel.Information, Message = "Added review comment to field submission {SubmissionId}; correctionRequest={CorrectionRequest}.")]
    public static partial void Commented(ILogger logger, Guid submissionId, bool correctionRequest);
}
