// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

internal sealed class OperationHandleStore
{
    private readonly ConcurrentDictionary<string, OperationHandle> _handles = new(StringComparer.Ordinal);

    public void Record(OperationHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _handles[handle.HandleId] = handle;
    }

    public OperationHandle? Get(string handleId)
        => _handles.TryGetValue(handleId, out var handle) ? handle : null;
}
