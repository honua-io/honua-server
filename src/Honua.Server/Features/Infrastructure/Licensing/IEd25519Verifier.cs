// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Licensing;

internal interface IEd25519Verifier
{
    bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature);
}
