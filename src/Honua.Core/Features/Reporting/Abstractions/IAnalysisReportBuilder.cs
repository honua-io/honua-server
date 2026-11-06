// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting.Domain;
using Honua.Core.Features.Security.Admission;

namespace Honua.Core.Features.Reporting.Abstractions;

public interface IAnalysisReportBuilder
{
    Task<AnalysisReport> BuildAsync(
        string jobId,
        AnalysisResultPackage package,
        CancellationToken cancellationToken);
    Task<AnalysisReport> BuildAsync(string jobId, AnalysisResultPackage package,
        AiExecutionContext? executionContext, CancellationToken cancellationToken)
        => BuildAsync(jobId, package, cancellationToken);
}
