// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geoprocessing.CustomCode;

// Anything other than a cryptographically valid signature fails closed, and the reason never carries secrets or a raw provider response.
public sealed record CommitSignatureResult(bool IsSignatureValid, string? SignerKeyId, string? Detail)
{
    public static CommitSignatureResult Unverifiable(string detail)
        => new(IsSignatureValid: false, SignerKeyId: null, Detail: detail);
}

public interface ICustomCodeCommitSignatureVerifier
{
    // An unverifiable commit returns a failed result instead of throwing, so the gate fails closed the same way every time.
    ValueTask<CommitSignatureResult> VerifyAsync(
        Uri repoUrl,
        string commitSha,
        CancellationToken cancellationToken);
}

// With no provider verifier wired, every commit is unverifiable, so signed-only blocks submission instead of admitting unsigned code.
internal sealed class UnverifiableCommitSignatureVerifier : ICustomCodeCommitSignatureVerifier
{
    public static readonly UnverifiableCommitSignatureVerifier Instance = new();

    public ValueTask<CommitSignatureResult> VerifyAsync(
        Uri repoUrl,
        string commitSha,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repoUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);

        return ValueTask.FromResult(CommitSignatureResult.Unverifiable(
            "no commit-signature verifier is configured; signed-only rejects unverifiable commits"));
    }
}
