// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Abstractions;

public interface IAnalysisReportStore
{
    Task<AnalysisReport?> TryGetAsync(
        string jobId,
        string contractVersion,
        CancellationToken cancellationToken);

    // Stores must be safe for concurrent calls on different job ids.
    Task StoreAsync(AnalysisReport report, CancellationToken cancellationToken);

    Task RemoveAsync(string jobId, CancellationToken cancellationToken);
}
