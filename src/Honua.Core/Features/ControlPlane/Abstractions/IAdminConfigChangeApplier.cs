// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Applies an admin configuration change described by an opaque, sanitized JSON
/// payload. Implementations route to the shared admin mutation pipeline. The
/// gateway invokes this on direct-execute and on proposal approval so the same
/// apply path runs in both cases (#1693).
/// </summary>
public interface IAdminConfigChangeApplier
{
    /// <summary>
    /// Applies the supplied configuration change.
    /// </summary>
    /// <param name="changePayload">Sanitized JSON describing the change.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A stable identifier for the applied change, when one exists.</returns>
    Task<string?> ApplyAsync(string? changePayload, CancellationToken cancellationToken = default);
}
