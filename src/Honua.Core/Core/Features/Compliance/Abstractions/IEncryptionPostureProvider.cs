// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Compliance.Domain;

namespace Honua.Core.Features.Compliance.Abstractions;

// This posture reports compliance evidence and does not hold cipher key material.
public interface IEncryptionPostureProvider
{
    EncryptionPosture GetPosture();

    // Recording a key-version rotation does not re-encrypt data or rotate the cipher material.
    Task<KeyRotationOutcome> RotateAsync(string requestedBy, CancellationToken cancellationToken = default);
}
