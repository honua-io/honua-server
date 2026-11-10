// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AnomalyDetection.Domain;

namespace Honua.Core.Features.AnomalyDetection.Abstractions;

public interface IAnomalyAnalyzer
{
    Task<AnomalyReport> AnalyzeAsync(
        AnomalyAnalysisRequest request,
        CancellationToken cancellationToken = default);
}
