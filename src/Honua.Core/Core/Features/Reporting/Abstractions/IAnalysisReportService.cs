// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Abstractions;

public interface IAnalysisReportService
{
    Task<AnalysisReport> GetReportAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task<RenderedAnalysisReport> GetRenderedAsync(
        string jobId,
        AnalysisReportFormat format,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);
}

public sealed record RenderedAnalysisReport(
    AnalysisReport Report,
    AnalysisReportFormat Format,
    string Body);
