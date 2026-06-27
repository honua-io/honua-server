// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.EmbedGovernance.Domain;

namespace Honua.Core.Features.EmbedGovernance.Abstractions;

/// <summary>
/// Result of creating or rotating an embed key, including one-time plaintext.
/// </summary>
/// <param name="Record">The persisted key metadata.</param>
/// <param name="Key">The plaintext key material (returned once).</param>
public sealed record EmbedKeyCreateResult(EmbedKeyRecord Record, string Key);

/// <summary>
/// Result of validating presented embed key material against the store.
/// </summary>
/// <param name="Record">The matched, still-active key record.</param>
public sealed record EmbedKeyValidationResult(EmbedKeyRecord Record);

/// <summary>
/// Issuance, lifecycle, validation, and rate accounting for embed API keys.
/// </summary>
public interface IEmbedKeyStore
{
    /// <summary>Lists all embed keys, oldest first.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<EmbedKeyRecord>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Creates a new embed key with the supplied scope.</summary>
    /// <param name="name">Human-readable key name.</param>
    /// <param name="scope">Authoritative key scope.</param>
    /// <param name="expiresAt">Optional expiration.</param>
    /// <param name="createdBy">Creating principal, when known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<EmbedKeyCreateResult> CreateAsync(
        string name,
        EmbedKeyScope scope,
        DateTimeOffset? expiresAt,
        string? createdBy,
        CancellationToken cancellationToken);

    /// <summary>Gets a single key by id.</summary>
    /// <param name="id">Key id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<EmbedKeyRecord?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Rotates a key, returning new plaintext and invalidating the old secret.</summary>
    /// <param name="id">Key id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<EmbedKeyCreateResult?> RotateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Revokes a key.</summary>
    /// <param name="id">Key id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<EmbedKeyRecord?> RevokeAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Validates presented plaintext key material.</summary>
    /// <param name="keyMaterial">The plaintext key from the embed request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<EmbedKeyValidationResult?> ValidateAsync(string keyMaterial, CancellationToken cancellationToken);

    /// <summary>
    /// Records a request against the key's fixed rate window and returns the
    /// number of requests consumed in the current window, including this one.
    /// </summary>
    /// <param name="id">Key id.</param>
    /// <param name="window">The fixed window length.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int> RecordRequestAsync(Guid id, TimeSpan window, CancellationToken cancellationToken);
}
