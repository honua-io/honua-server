// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text;
using Honua.Core.Features.Reporting.Domain;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Geoprocessing;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Reporting.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Reporting;

/// <summary>
/// Minimal API endpoints for the analysis-reporting feature. Exposes the JSON
/// envelope and the rendered Markdown / HTML bodies. Authorization inherits
/// from the result-package surface via <see cref="IAnalysisReportService"/>.
/// </summary>
internal static class AnalysisReportEndpoints
{
    private static readonly ActivitySource _activitySource = AnalysisReportService.ActivitySource;

    /// <summary>
    /// Maps the report endpoints under <c>/api/v{version}/analysis/reports</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapAnalysisReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/analysis/reports/{jobId}")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Analysis", "Reports")
            .WithDescription("Analysis report envelopes derived from completed geoprocessing jobs.")
            .RequireAdminAuthorization();

        _ = group.MapGet("", HandleGetReportAsync)
            .WithName("GetAnalysisReport")
            .WithSummary("Retrieve the structured analysis report envelope for a completed job.");

        _ = group.MapGet("/render", HandleRenderAsync)
            .WithName("RenderAnalysisReport")
            .WithSummary("Render the analysis report as Markdown or HTML.");

        return endpoints;
    }

    private static async Task<IResult> HandleGetReportAsync(
        string jobId,
        [FromServices] IAnalysisReportService reportService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity("Reporting.HttpGetReport");
        activity?.SetTag("report.job_id", jobId);

        try
        {
            var report = await reportService
                .GetReportAsync(jobId, httpContext.User, cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(report, ReportingJsonContext.Default.AnalysisReport);
        }
        catch (Exception ex) when (TryMapException(ex, httpContext, out var problem))
        {
            return problem;
        }
    }

    private static async Task<IResult> HandleRenderAsync(
        string jobId,
        [FromQuery] string format,
        [FromServices] IAnalysisReportService reportService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var resolvedFormat = ParseFormat(format);
        if (resolvedFormat is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                $"Unsupported render format '{format}'. Allowed values: md, html.");
        }

        using var activity = _activitySource.StartActivity("Reporting.HttpRender");
        activity?.SetTag("report.job_id", jobId);
        activity?.SetTag("report.format", resolvedFormat.Value.ToString());

        try
        {
            var rendered = await reportService
                .GetRenderedAsync(jobId, resolvedFormat.Value, httpContext.User, cancellationToken)
                .ConfigureAwait(false);

            var contentType = resolvedFormat.Value == AnalysisReportFormat.Html
                ? "text/html; charset=utf-8"
                : "text/markdown; charset=utf-8";
            return Results.Text(rendered.Body, contentType, Encoding.UTF8);
        }
        catch (UnsupportedReportContractVersionException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status409Conflict,
                UnsupportedReportContractVersionException.Code,
                ex.Message);
        }
        catch (Exception ex) when (TryMapException(ex, httpContext, out var problem))
        {
            return problem;
        }
    }

    private static AnalysisReportFormat? ParseFormat(string? format) => format?.ToLowerInvariant() switch
    {
        "md" or "markdown" or "text/markdown" => AnalysisReportFormat.Markdown,
        "html" or "text/html" => AnalysisReportFormat.Html,
        _ => null
    };

    private static bool TryMapException(Exception ex, HttpContext httpContext, out IResult problem)
    {
        switch (ex)
        {
            case GeoprocessingAuthorizationException authEx when authEx.RequiresAuthentication:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status401Unauthorized,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status401Unauthorized),
                    authEx.Message);
                return true;
            case GeoprocessingAuthorizationException authEx:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status403Forbidden,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status403Forbidden),
                    authEx.Message);
                return true;
            case GeoprocessingNotFoundException notFoundEx:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status404NotFound,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                    notFoundEx.Message);
                return true;
            case GeoprocessingPreconditionFailedException preconditionEx:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status409Conflict,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status409Conflict),
                    preconditionEx.Message);
                return true;
            case GeoprocessingValidationException validationEx:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status400BadRequest,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                    validationEx.Message);
                return true;
            case GeoprocessingStoreUnavailableException storeEx:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status503ServiceUnavailable,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                    storeEx.Message);
                return true;
            default:
                problem = Results.Empty;
                return false;
        }
    }
}
