// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Alerts.Abstractions;

public interface IAlertPipeline
{
    Task<long> ProcessChangesAsync(
        long lastProcessedGeneration,
        int batchSize,
        DateTimeOffset? evaluatedAt = null,
        CancellationToken cancellationToken = default);

    Task<int> SweepDwellAsync(
        DateTimeOffset evaluatedAt,
        int batchSize,
        CancellationToken cancellationToken = default);
}
