// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Receives notification when a durable execution job transitions to a terminal
/// state. Implementations synchronize feature-specific side-effect stores
/// (e.g. admin progress) with the authoritative execution job record.
/// </summary>
/// <remarks>
/// Callbacks are invoked best-effort after the authoritative store write.
/// A failing callback must not block the terminal transition.
/// </remarks>
public interface IJobTerminalCallback
{
    /// <summary>
    /// Called after a job has been durably written to a terminal status.
    /// </summary>
    ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken);
}
