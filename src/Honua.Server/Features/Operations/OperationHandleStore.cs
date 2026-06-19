// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Minimal in-memory store of operation handles so the status endpoint
/// (<c>GET /api/v1/operations/handles/{handleId}</c>) can look a submitted handle back up.
/// This is a de-risking-spike scaffold: a durable handle/job store lands with the job-based
/// execution kinds in a later phase.
/// </summary>
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
