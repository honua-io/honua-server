// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Abstractions;

public interface IDistributedJobQueueService
{
    Task EnqueueAsync(string jobId, CancellationToken cancellationToken = default);

    Task<string?> DequeueAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    Task CompleteAsync(string jobId, CancellationToken cancellationToken = default);

    Task RecoverInFlightAsync(CancellationToken cancellationToken = default);

    Task<long> GetQueueLengthAsync(CancellationToken cancellationToken = default);
}
