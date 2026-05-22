// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Compliance.Domain;

namespace Honua.Core.Features.Compliance.Abstractions;

/// <summary>
/// Reports the encryption-at-rest posture (FIPS mode, algorithms, key versions) and
/// orchestrates zero-downtime key rotation. Implementations must keep the host
/// available across rotations — historical key versions remain valid for decryption.
/// </summary>
public interface IEncryptionPostureProvider
{
    /// <summary>Read the current posture. Cheap — used for dashboard polling.</summary>
    EncryptionPosture GetPosture();

    /// <summary>
    /// Rotate the active encryption key. New encryptions use the new version; existing
    /// ciphertexts continue to decrypt against their original version (no migration
    /// downtime). The implementation persists the new version metadata.
    /// </summary>
    Task<KeyRotationOutcome> RotateAsync(string requestedBy, CancellationToken cancellationToken = default);
}
