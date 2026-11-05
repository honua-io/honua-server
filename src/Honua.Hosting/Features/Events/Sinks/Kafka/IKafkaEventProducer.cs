// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Events;

internal interface IKafkaEventProducer : IAsyncDisposable
{
    Task ProduceAsync(
        string topic,
        string key,
        byte[] value,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken = default);
}
