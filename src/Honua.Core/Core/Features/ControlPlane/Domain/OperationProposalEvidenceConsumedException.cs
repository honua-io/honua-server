// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

// Signed proposal evidence is single-use: a store throws this when the evidence already backs another
// proposal, or when it lacks the tenant, transcript key or run nonce that identify it.
public sealed class OperationProposalEvidenceConsumedException(string message) : InvalidOperationException(message)
{
    public const string AlreadyUsedMessage = "The signed proposal evidence has already been used.";
}
