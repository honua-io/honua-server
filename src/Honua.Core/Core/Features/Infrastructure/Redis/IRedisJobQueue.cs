// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Infrastructure.Redis;

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Queue is the established domain term for this Redis abstraction.")]
public interface IRedisJobQueue : IRedisService
{
    string QueueKey { get; }

    Task EnqueueAsync(string job, CancellationToken cancellationToken = default);

    Task<string?> DequeueAsync(CancellationToken cancellationToken = default);

    Task<long> GetQueueLengthAsync(CancellationToken cancellationToken = default);

    Task<long> GetProcessingCountAsync(CancellationToken cancellationToken = default);
}
