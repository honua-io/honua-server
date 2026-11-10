// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Abstractions;

public interface IDistributedProgressStore<TProgress> where TProgress : class
{
    Task SetProgressAsync(string jobId, TProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default);

    Task<TProgress?> GetProgressAsync(string jobId, CancellationToken cancellationToken = default);

    Task DeleteProgressAsync(string jobId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetActiveJobIdsAsync(CancellationToken cancellationToken = default);
}
