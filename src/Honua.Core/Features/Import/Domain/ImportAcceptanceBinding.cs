// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Server-issued identity for one durable import acceptance. The coordinator persists
/// this binding with the immutable upload intent before submitting the job. It is not
/// derived from an untrusted upload correlation ID or accepted from client JSON.
/// </summary>
/// <param name="OperationId">Canonical server-minted job identifier.</param>
/// <param name="IntentSha256">Digest of the frozen source, destination, options and ownership.</param>
/// <param name="AcceptBefore">Deadline for first acceptance; shorter than job receipt retention.</param>
public sealed record ImportAcceptanceBinding(string OperationId, string IntentSha256, DateTimeOffset AcceptBefore)
{
    /// <summary>Checks the canonical identifier and intent representation without authorizing execution.</summary>
    public void Validate()
    {
        if (OperationId is null || OperationId.Length != 32 || OperationId.Any(static value => !char.IsAsciiHexDigitLower(value)) ||
            IntentSha256 is null || IntentSha256.Length != 64 || IntentSha256.Any(static value => !char.IsAsciiHexDigitLower(value)))
        {
            throw new ArgumentException("A canonical import acceptance identity and SHA-256 intent are required.");
        }
    }
}
