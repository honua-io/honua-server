// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.AgentWorkspace;

/// <summary>
/// Host-owned readiness decision for the complete durable import execution path.
/// Registration alone is not readiness. There is intentionally no default adapter.
/// </summary>
/// <remarks>
/// An implementation must verify the initialized source storage and budget, bounded
/// processing scratch factory, actual durable worker and queue, execution commit
/// authority and committed-result reader, and execution/historical observation
/// policy and kind providers. It must return false while any required dependency
/// is unavailable. A request or client capability must never configure this port.
/// </remarks>
internal interface IDatasetImportSubmissionReadiness
{
    ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken = default);
}
