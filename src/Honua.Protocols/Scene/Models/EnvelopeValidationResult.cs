// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Scene.Models;

// HMAC is verified before any field check so a forged signature reveals nothing about the token contents.
internal enum EnvelopeValidationResult
{
    Allowed,

    Expired,

    Tampered,

    WrongScene
}
