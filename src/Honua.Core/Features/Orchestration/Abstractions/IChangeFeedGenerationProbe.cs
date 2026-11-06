// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Orchestration.Abstractions;

public interface IChangeFeedGenerationProbe
{
    Task<long?> GetLatestGenerationAsync(
        long sinceGeneration,
        IReadOnlyList<int> watchedLayerIds,
        CancellationToken cancellationToken = default);
}
