// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Abstractions;

/// <summary>
/// Composes an <see cref="AnalysisReport"/> from an
/// <see cref="AnalysisResultPackage"/>. Resolves the template, runs the
/// narrative path, and stamps the result with template + contract version
/// metadata.
/// </summary>
public interface IAnalysisReportBuilder
{
    /// <summary>
    /// Builds a report for <paramref name="package"/>. Returns the structured
    /// envelope; renderers convert to format-specific output.
    /// </summary>
    Task<AnalysisReport> BuildAsync(
        string jobId,
        AnalysisResultPackage package,
        CancellationToken cancellationToken);
}
