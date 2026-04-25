// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Reporting.Domain;

namespace Honua.Server.Features.Reporting;

/// <summary>
/// Server-side orchestrator for analysis-report retrieval. Wraps the core
/// builder so endpoints and the MCP resource only depend on a single service.
/// </summary>
internal interface IAnalysisReportService
{
    /// <summary>
    /// Retrieves the canonical <see cref="AnalysisReport"/> envelope for the
    /// supplied job id. Authorization is delegated to the geoprocessing job
    /// service so reports inherit result-package ACLs.
    /// </summary>
    Task<AnalysisReport> GetReportAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves the rendered body for the supplied job id and format. Caches
    /// rendered output keyed by <c>(jobId, contractVersion, format,
    /// resultPackageId)</c>.
    /// </summary>
    Task<RenderedAnalysisReport> GetRenderedAsync(
        string jobId,
        AnalysisReportFormat format,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);
}

/// <summary>
/// Rendered analysis report body plus the contract metadata required to
/// surface the right MIME type and HTTP cache headers.
/// </summary>
internal sealed record RenderedAnalysisReport(
    AnalysisReport Report,
    AnalysisReportFormat Format,
    string Body);
