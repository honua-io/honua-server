// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Events;

internal interface INatsEventProducer : IAsyncDisposable
{
    Task PublishAsync(
        string subject,
        string messageId,
        byte[] value,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken = default);
}
