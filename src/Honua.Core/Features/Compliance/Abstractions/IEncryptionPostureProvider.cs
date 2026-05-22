// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Compliance.Domain;

namespace Honua.Core.Features.Compliance.Abstractions;

/// <summary>
/// Reports the compliance encryption-at-rest posture (FIPS mode, algorithms, key
/// versions) and advances the auditor-facing key-version counter when a rotation
/// is requested. This abstraction is the source of truth for evidence rows that
/// auditors review; it does not own actual cipher key material — that lives in
/// <c>Honua.Postgres.Features.Security.IConnectionEncryptionService</c>.
/// </summary>
public interface IEncryptionPostureProvider
{
    /// <summary>Read the current posture. Cheap — used for dashboard polling.</summary>
    EncryptionPosture GetPosture();

    /// <summary>
    /// Advance the compliance encryption posture by recording a new key-version
    /// rotation event. The implementation appends the version to its ring, marks
    /// the previous version retired in the posture timeline, and emits the
    /// auditor-required <c>encryption.key.rotate</c> audit event.
    /// </summary>
    /// <remarks>
    /// This method does not re-encrypt data or rotate the cipher material used by
    /// the connection-encryption service. Real key-material rotation is the
    /// responsibility of <c>IConnectionEncryptionService</c>; this endpoint advances
    /// the posture counter and the audit trail so SOC 2 / FedRAMP evidence reflects
    /// the operator action.
    /// </remarks>
    Task<KeyRotationOutcome> RotateAsync(string requestedBy, CancellationToken cancellationToken = default);
}
