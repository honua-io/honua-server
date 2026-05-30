// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Compliance.Abstractions;
using Honua.Core.Features.Compliance.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints exposing the compliance dashboard, residency evaluator, key
/// rotation, and report exporter. The endpoints are intentionally thin — every
/// non-trivial decision happens in <c>Honua.Core.Features.Compliance</c> so the
/// same logic powers MCP / SDK callers when those land.
/// </summary>
internal static partial class ComplianceAdminEndpoints
{
    private const string CsvContentType = "text/csv; charset=utf-8";
    private const string PdfContentType = "application/pdf";

    internal sealed class ComplianceAdminEndpointsLog;

    internal static partial class ComplianceAdminLog
    {
        [LoggerMessage(EventId = 4710, Level = LogLevel.Information,
            Message = "Compliance dashboard requested by {Actor}")]
        public static partial void DashboardRequested(ILogger logger, string actor);

        [LoggerMessage(EventId = 4711, Level = LogLevel.Information,
            Message = "Compliance report exported as {Format} by {Actor}")]
        public static partial void ReportExported(ILogger logger, string format, string actor);

        [LoggerMessage(EventId = 4712, Level = LogLevel.Information,
            Message = "Compliance residency evaluation for region '{Region}' by {Actor}: allowed={Allowed}")]
        public static partial void ResidencyEvaluated(ILogger logger, string region, string actor, bool allowed);

        [LoggerMessage(EventId = 4713, Level = LogLevel.Information,
            Message = "Compliance key rotation requested by {Actor}: from v{Previous} to v{Next}")]
        public static partial void KeyRotated(ILogger logger, string actor, int previous, int next);

        [LoggerMessage(EventId = 4714, Level = LogLevel.Warning,
            Message = "Compliance report requested in unsupported format '{Format}' by {Actor}")]
        public static partial void UnsupportedReportFormat(ILogger logger, string format, string actor);
    }

    public static void MapComplianceAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/compliance")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Compliance")
            .RequireAdminAuthorization();

        group.MapGet("/dashboard", HandleGetDashboard)
            .WithDisplayName("Get Compliance Dashboard")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<ComplianceDashboardResponse>>();

        group.MapGet("/report", HandleExportReport)
            .WithDisplayName("Export Compliance Report")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces(StatusCodes.Status200OK, contentType: PdfContentType)
            .Produces(StatusCodes.Status200OK, contentType: CsvContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status406NotAcceptable);

        group.MapPost("/residency/evaluate", HandleEvaluateResidency)
            .WithDisplayName("Evaluate Residency Policy")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<ApiResponse<ComplianceResidencyEvaluationResponse>>();

        group.MapPost("/encryption/rotate-key", HandleRotateKey)
            .WithDisplayName("Rotate Encryption Key (Compliance)")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<ApiResponse<ComplianceKeyRotationResponse>>();
    }

    private static async Task<IResult> HandleGetDashboard(
        [FromServices] IComplianceEvidenceCollector collector,
        [FromServices] ILogger<ComplianceAdminEndpointsLog> logger,
        HttpContext context)
    {
        var actor = context.GetUserIdentity();
        ComplianceAdminLog.DashboardRequested(logger, actor);

        var snapshot = await collector.CollectAsync(context.RequestAborted).ConfigureAwait(false);
        var dashboard = ProjectDashboard(snapshot);

        return Results.Json(
            ApiResponse<ComplianceDashboardResponse>.CreateSuccess(dashboard),
            ComplianceAdminJsonContext.Default.ApiResponseComplianceDashboardResponse);
    }

    private static async Task<IResult> HandleExportReport(
        [FromQuery] string? format,
        [FromServices] IComplianceReportExporter exporter,
        [FromServices] IAuditLog auditLog,
        [FromServices] ILogger<ComplianceAdminEndpointsLog> logger,
        HttpContext context)
    {
        var actor = context.GetUserIdentity();

        if (!TryParseFormat(format, out var parsed))
        {
            ComplianceAdminLog.UnsupportedReportFormat(logger, format ?? "<null>", actor);
            return Results.BadRequest(ApiResponse<object>.Failure(
                "Compliance report format must be 'pdf' or 'csv'."));
        }

        var formatName = parsed.ToString();
        ComplianceReportArtifact artifact;
        try
        {
            artifact = await exporter.ExportAsync(parsed, context.RequestAborted).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            // The format string parsed but no renderer is registered for it (e.g. a
            // deployment trimmed the renderer enumerable). 406 is the correct status
            // for media-type negotiation failure; 400 is reserved for the parser
            // rejection path above (unrecognized format string).
            ComplianceAdminLog.UnsupportedReportFormat(logger, formatName, actor);
            return Results.Json(
                ApiResponse<object>.Failure(
                    "Requested compliance report format is not supported by this deployment."),
                ComplianceAdminJsonContext.Default.ApiResponseObject,
                statusCode: StatusCodes.Status406NotAcceptable);
        }

        ComplianceAdminLog.ReportExported(logger, formatName, actor);
        await auditLog.RecordAsync(new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.DataExport,
            Actor = actor,
            ActorType = AuditActorType.UserId,
            ResourceType = "compliance-report",
            ResourceId = formatName,
            Action = "compliance.report.export",
            Outcome = AuditOutcome.Success,
            CorrelationId = context.TraceIdentifier,
            Details = $"{{\"format\":\"{formatName.ToLowerInvariant()}\",\"bytes\":{artifact.Content.Length}}}",
        }, context.RequestAborted).ConfigureAwait(false);

        return Results.File(
            artifact.Content,
            contentType: artifact.ContentType,
            fileDownloadName: $"{artifact.FileNameStem}.{artifact.FileExtension}");
    }

    private static async Task<IResult> HandleEvaluateResidency(
        [FromServices] IDataResidencyPolicyProvider provider,
        [FromServices] IAuditLog auditLog,
        [FromServices] ILogger<ComplianceAdminEndpointsLog> logger,
        HttpContext context)
    {
        var actor = context.GetUserIdentity();
        ComplianceResidencyEvaluationRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                ComplianceAdminJsonContext.Default.ComplianceResidencyEvaluationRequest,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(ApiResponse<object>.Failure(
                "Residency evaluation body must be valid JSON with a 'region' field."));
        }

        var region = request?.Region ?? string.Empty;
        var decision = provider.Evaluate(region);

        ComplianceAdminLog.ResidencyEvaluated(logger, region, actor, decision.Allowed);
        await auditLog.RecordAsync(new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.AdminAction,
            Actor = actor,
            ActorType = AuditActorType.UserId,
            ResourceType = "residency-policy",
            ResourceId = region,
            Action = "compliance.residency.evaluate",
            Outcome = decision.Allowed ? AuditOutcome.Success : AuditOutcome.Denied,
            CorrelationId = context.TraceIdentifier,
            Details = decision.Reason,
        }, context.RequestAborted).ConfigureAwait(false);

        var response = new ComplianceResidencyEvaluationResponse
        {
            Allowed = decision.Allowed,
            Region = decision.Region,
            Reason = decision.Reason,
            Policy = ProjectResidency(decision.Policy),
        };

        return Results.Json(
            ApiResponse<ComplianceResidencyEvaluationResponse>.CreateSuccess(response),
            ComplianceAdminJsonContext.Default.ApiResponseComplianceResidencyEvaluationResponse);
    }

    private static async Task<IResult> HandleRotateKey(
        [FromServices] IEncryptionPostureProvider posture,
        [FromServices] ILogger<ComplianceAdminEndpointsLog> logger,
        HttpContext context)
    {
        var actor = context.GetUserIdentity();
        var outcome = await posture.RotateAsync(actor, context.RequestAborted).ConfigureAwait(false);
        ComplianceAdminLog.KeyRotated(logger, actor, outcome.PreviousVersion, outcome.NewVersion);

        var response = new ComplianceKeyRotationResponse
        {
            Succeeded = outcome.Succeeded,
            PreviousVersion = outcome.PreviousVersion,
            NewVersion = outcome.NewVersion,
            RotatedAt = outcome.RotatedAt,
            Message = outcome.Message,
        };

        return Results.Json(
            ApiResponse<ComplianceKeyRotationResponse>.CreateSuccess(response),
            ComplianceAdminJsonContext.Default.ApiResponseComplianceKeyRotationResponse);
    }

    private static bool TryParseFormat(string? value, out ComplianceReportFormat parsed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = ComplianceReportFormat.Pdf;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "pdf":
                parsed = ComplianceReportFormat.Pdf;
                return true;
            case "csv":
                parsed = ComplianceReportFormat.Csv;
                return true;
            default:
                parsed = ComplianceReportFormat.Pdf;
                return false;
        }
    }

    private static ComplianceDashboardResponse ProjectDashboard(ComplianceSnapshot snapshot)
    {
        var controls = new List<ComplianceControlView>(snapshot.Controls.Count);
        foreach (var row in snapshot.Controls)
        {
            var evidence = new List<ComplianceEvidenceView>(row.Evidence.Count);
            foreach (var item in row.Evidence)
            {
                evidence.Add(new ComplianceEvidenceView
                {
                    Source = item.Source,
                    CollectedAt = item.CollectedAt,
                    Claim = item.Claim,
                    Status = item.Status.ToString(),
                    Detail = item.Detail,
                });
            }

            controls.Add(new ComplianceControlView
            {
                Framework = row.Control.Framework.ToString(),
                ControlId = row.Control.ControlId,
                Title = row.Control.Title,
                Description = row.Control.Description,
                Status = row.Status.ToString(),
                RelatedControls = row.Control.RelatedControls,
                Gaps = row.Gaps,
                Evidence = evidence.AsReadOnly(),
            });
        }

        return new ComplianceDashboardResponse
        {
            GeneratedAt = snapshot.GeneratedAt,
            ServerVersion = snapshot.ServerVersion,
            Summary = new ComplianceSummaryView
            {
                Implemented = snapshot.Summary.Implemented,
                PartiallyImplemented = snapshot.Summary.PartiallyImplemented,
                NotImplemented = snapshot.Summary.NotImplemented,
                NotApplicable = snapshot.Summary.NotApplicable,
                Unknown = snapshot.Summary.Unknown,
                ReadinessPercent = snapshot.Summary.ReadinessPercent,
            },
            Encryption = new ComplianceEncryptionView
            {
                FipsMode = snapshot.Encryption.FipsMode,
                FipsSource = snapshot.Encryption.FipsSource,
                Algorithms = snapshot.Encryption.Algorithms,
                ActiveKeyVersion = snapshot.Encryption.ActiveKeyVersion,
                RetainedKeyVersions = snapshot.Encryption.RetainedKeyVersions,
                ActiveKeyIssuedAt = snapshot.Encryption.ActiveKeyIssuedAt,
                LastRotationAt = snapshot.Encryption.LastRotationAt,
            },
            Residency = ProjectResidency(snapshot.Residency),
            Controls = controls.AsReadOnly(),
        };
    }

    private static ComplianceResidencyView ProjectResidency(DataResidencyPolicy policy) => new()
    {
        Enforced = policy.Enforced,
        PrimaryRegion = policy.PrimaryRegion,
        AllowedRegions = policy.AllowedRegions,
    };
}
