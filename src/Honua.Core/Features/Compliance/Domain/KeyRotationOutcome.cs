// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

/// <summary>
/// Result of a key rotation request. Both successful and pre-condition failures
/// produce an outcome record so the caller can render a deterministic response.
/// </summary>
public sealed record KeyRotationOutcome
{
    /// <summary>Whether the rotation succeeded.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Key version before the rotation attempt.</summary>
    public required int PreviousVersion { get; init; }

    /// <summary>
    /// Key version after the rotation. Equals <see cref="PreviousVersion"/> when
    /// <see cref="Succeeded"/> is <c>false</c>.
    /// </summary>
    public required int NewVersion { get; init; }

    /// <summary>UTC timestamp the rotation was attempted.</summary>
    public required DateTimeOffset RotatedAt { get; init; }

    /// <summary>
    /// Reason / status message. On failure, this never contains the underlying
    /// exception text — call-sites translate exceptions to a sanitized reason.
    /// </summary>
    public required string Message { get; init; }
}
