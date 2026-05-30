// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.Scene.Models;

/// <summary>
/// Outcome of validating an inbound scene access envelope token.
/// </summary>
/// <remarks>
/// HMAC verification runs before any field-level check so the verifier
/// never returns information about token contents on a forged signature.
/// </remarks>
internal enum EnvelopeValidationResult
{
    /// <summary>
    /// Token is well-formed, signed by the expected key, unexpired, and
    /// scoped to the requested scene.
    /// </summary>
    Allowed,

    /// <summary>
    /// Token decoded and verified but its <c>expiresAt</c> instant is in
    /// the past relative to the configured <see cref="TimeProvider"/>.
    /// </summary>
    Expired,

    /// <summary>
    /// Token failed structural decoding or HMAC verification. Returned
    /// for any signature, payload, or encoding fault to deny field-level
    /// oracle attacks.
    /// </summary>
    Tampered,

    /// <summary>
    /// Token verified but is bound to a different scene than the request.
    /// </summary>
    WrongScene
}
