// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

/// <summary>
/// Snapshot of the deployment's encryption posture — the inputs FedRAMP and SOC 2
/// auditors expect to see itemised in the system security plan.
/// </summary>
public sealed record EncryptionPosture
{
    /// <summary>
    /// Whether the .NET runtime reports FIPS-validated cryptography is in use.
    /// Sourced from <c>System.Security.Cryptography.CryptographyConfig.AllowOnlyFipsAlgorithms</c>
    /// and OS-level signals (e.g. <c>/proc/sys/crypto/fips_enabled</c> on Linux).
    /// </summary>
    public required bool FipsMode { get; init; }

    /// <summary>
    /// The mechanism that established <see cref="FipsMode"/> (e.g. <c>os-fips-enabled</c>,
    /// <c>runtime-policy</c>, <c>unverified</c>). Surfaced verbatim in audit reports.
    /// </summary>
    public required string FipsSource { get; init; }

    /// <summary>
    /// Canonical algorithms used for encryption-at-rest. Each entry has the shape
    /// <c>aes-256-gcm</c>, <c>pbkdf2-sha256</c>, etc. — lower-case hyphen-separated.
    /// </summary>
    public required IReadOnlyList<string> Algorithms { get; init; }

    /// <summary>Current active encryption-at-rest key version.</summary>
    public required int ActiveKeyVersion { get; init; }

    /// <summary>Total number of historical key versions retained for decryption of older ciphertexts.</summary>
    public required int RetainedKeyVersions { get; init; }

    /// <summary>UTC timestamp the active key was issued. <c>null</c> if no rotation has occurred.</summary>
    public DateTimeOffset? ActiveKeyIssuedAt { get; init; }

    /// <summary>UTC timestamp the active key was last rotated. <c>null</c> if no rotation has occurred.</summary>
    public DateTimeOffset? LastRotationAt { get; init; }
}
