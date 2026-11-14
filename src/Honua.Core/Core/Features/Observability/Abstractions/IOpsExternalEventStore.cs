// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Features.Observability.Abstractions;

public interface IOpsExternalEventStore
{
    Task AppendAsync(OpsExternalEvent externalEvent, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OpsExternalEvent>> ReadRecentAsync(DateTimeOffset since, CancellationToken cancellationToken = default);
}

public interface IOpsEventQueueClient
{
    Task<IReadOnlyList<OpsEventQueueMessage>> ReceiveAsync(CancellationToken cancellationToken);

    Task DeleteAsync(string receiptHandle, CancellationToken cancellationToken);
}

// Called after durable ingestion and before acknowledgement, including duplicate deliveries.
// Implementations must be idempotent by event id and fence the target release themselves.
public interface IOpsExternalEventHandler
{
    Task HandleAsync(OpsExternalEvent externalEvent, CancellationToken cancellationToken);
}
