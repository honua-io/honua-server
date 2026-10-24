// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Honua.Infrastructure.Licensing;

internal sealed class BouncyCastleEd25519Verifier : IEd25519Verifier
{
    public bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64)
        {
            return false;
        }

        var keyParameters = new Ed25519PublicKeyParameters(publicKey.ToArray(), 0);
        var verifier = new Ed25519Signer();
        verifier.Init(false, keyParameters);

        var payloadBytes = payload.ToArray();
        verifier.BlockUpdate(payloadBytes, 0, payloadBytes.Length);
        return verifier.VerifySignature(signature.ToArray());
    }
}
