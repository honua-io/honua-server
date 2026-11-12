// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Events;

// Durable positions of server-side stream consumers.
internal interface IStreamCursorStore
{
    Task<long?> ReadAsync(string name, CancellationToken ct);

    Task WriteAsync(string name, long position, CancellationToken ct);
}
