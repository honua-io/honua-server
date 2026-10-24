// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Licensing;

internal interface IEd25519Verifier
{
    bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature);
}
