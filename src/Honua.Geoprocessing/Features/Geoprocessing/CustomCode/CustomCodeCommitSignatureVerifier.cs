// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing.CustomCode;

/// <summary>
/// The outcome of verifying the signature on a pinned custom-code commit. This is the
/// seam the <see cref="CustomCodeRepoPolicy.SignedOnly"/> gate evaluates: a commit is
/// admitted only when <see cref="IsSignatureValid"/> is <see langword="true"/>
/// <em>and</em> <see cref="SignerKeyId"/> is on the configured
/// <see cref="CustomCodeOptions.TrustedSignerKeys"/>.
/// </summary>
/// <param name="IsSignatureValid">
/// <see langword="true"/> only when the verifier cryptographically confirmed a valid
/// signature over the commit object. <see langword="false"/> when the signature is
/// absent, malformed, cryptographically invalid, or could not be verified at all (the
/// verifier could not reach the provider) — the gate treats every
/// <see langword="false"/> the same: fail closed.
/// </param>
/// <param name="SignerKeyId">
/// The identifier of the key that produced the signature (GPG long key-id /
/// fingerprint, or sigstore identity) when <see cref="IsSignatureValid"/> is
/// <see langword="true"/>; otherwise <see langword="null"/>.
/// </param>
/// <param name="Detail">
/// A short, non-sensitive human-readable reason suitable for surfacing in the
/// rejection message (e.g. <c>"commit is not signed"</c>,
/// <c>"signature could not be verified (provider unreachable)"</c>). Never carry
/// secrets, tokens, or raw provider responses here.
/// </param>
public sealed record CommitSignatureResult(bool IsSignatureValid, string? SignerKeyId, string? Detail)
{
    /// <summary>
    /// A canonical "could not verify" result used by verifiers that cannot reach the
    /// provider or have no implementation for the repo host. Fails closed under
    /// <see cref="CustomCodeRepoPolicy.SignedOnly"/>.
    /// </summary>
    /// <param name="detail">The non-sensitive reason verification was not possible.</param>
    /// <returns>An invalid-signature result carrying <paramref name="detail"/>.</returns>
    public static CommitSignatureResult Unverifiable(string detail)
        => new(IsSignatureValid: false, SignerKeyId: null, Detail: detail);
}

/// <summary>
/// Verifies that a pinned custom-code commit carries a trusted, valid signature. This
/// is a deliberate seam: the concrete verifier may call the git provider's API (e.g.
/// GitHub's <c>verification</c> object on the commit), shell out to a sigstore/GPG
/// verifier, or consult an internal attestation store. The submit gate depends only
/// on this abstraction so the verification mechanism can evolve without touching the
/// policy. A verifier MUST fail closed: when it cannot establish a valid signature for
/// any reason it returns a result whose <see cref="CommitSignatureResult.IsSignatureValid"/>
/// is <see langword="false"/> (use <see cref="CommitSignatureResult.Unverifiable"/>).
/// </summary>
public interface ICustomCodeCommitSignatureVerifier
{
    /// <summary>
    /// Verifies the signature on the commit identified by <paramref name="commitSha"/>
    /// in the repository at <paramref name="repoUrl"/>.
    /// </summary>
    /// <param name="repoUrl">The validated absolute HTTPS repository URL.</param>
    /// <param name="commitSha">The validated full 40-hex commit SHA being pinned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The verification outcome. Implementations must never throw for an
    /// unverifiable commit — return <see cref="CommitSignatureResult.Unverifiable"/>
    /// instead so the gate fails closed deterministically.
    /// </returns>
    ValueTask<CommitSignatureResult> VerifyAsync(
        Uri repoUrl,
        string commitSha,
        CancellationToken cancellationToken);
}

/// <summary>
/// The default verifier registered when no provider-backed verifier is configured. It
/// performs no network I/O and always reports the commit as unverifiable, so a
/// deployment that selects <see cref="CustomCodeRepoPolicy.SignedOnly"/> without
/// wiring a real verifier rejects every submission (fail-closed) rather than silently
/// admitting unsigned code. This keeps signed-only honest by construction: opting into
/// the strongest posture without a verifier is a no-op that blocks, never a bypass.
/// </summary>
internal sealed class UnverifiableCommitSignatureVerifier : ICustomCodeCommitSignatureVerifier
{
    /// <summary>The shared singleton instance (the verifier is stateless).</summary>
    public static readonly UnverifiableCommitSignatureVerifier Instance = new();

    /// <inheritdoc />
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
