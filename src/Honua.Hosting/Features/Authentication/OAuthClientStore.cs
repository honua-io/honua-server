// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// First-class OAuth2 client registry (ADR-0053 Increment 2, #1888). Unlike
/// Increment 1 — which reused the Admin API-key store as the
/// <c>client_credentials</c> secret — this is a real per-application client
/// identity: a distinct <c>client_id</c>/<c>client_secret</c> pair with
/// client-type metadata, allowed grant types, redirect URIs, and the subset of
/// the scope catalogue the client may request.
/// </summary>
/// <remarks>
/// The secret is never stored in plaintext: it is SHA-256 hashed at rest and
/// compared with <see cref="CryptographicOperations.FixedTimeEquals"/>, exactly
/// like the Admin API-key store. Validation honours expiry and revocation. The
/// store is in-memory to mirror the established <see cref="IAdminApiKeyStore"/>
/// pattern (no parallel durable token store per ADR-0049); a future increment may
/// back it with the durable store without changing this contract.
/// </remarks>
internal interface IOAuthClientStore
{
    /// <summary>Lists all registered clients (secrets never returned).</summary>
    Task<IReadOnlyList<OAuthClientRecord>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Registers a new client and returns its one-time plaintext secret.</summary>
    Task<OAuthClientCreateResult> CreateAsync(
        OAuthClientRegistration registration,
        CancellationToken cancellationToken);

    /// <summary>Fetches a client by id (secret never returned).</summary>
    Task<OAuthClientRecord?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Deletes a registered client. Returns the removed record, or null.</summary>
    Task<OAuthClientRecord?> DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Validates a presented <c>client_id</c>/<c>client_secret</c> pair. Returns the
    /// client record when the secret matches and the client is neither revoked nor
    /// expired; otherwise <see langword="null"/> (callers must not distinguish
    /// unknown-client from bad-secret — RFC 6749 §5.2 <c>invalid_client</c>).
    /// </summary>
    Task<OAuthClientRecord?> ValidateSecretAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken);
}

/// <summary>OAuth2 client type (RFC 6749 §2.1).</summary>
internal enum OAuthClientType
{
    /// <summary>Holds a secret securely (server-side service). The default.</summary>
    Confidential = 0,

    /// <summary>Cannot keep a secret (native/SPA). No secret is minted/required.</summary>
    Public = 1,
}

/// <summary>Inputs to register a first-class OAuth2 client.</summary>
internal sealed record OAuthClientRegistration(
    string Name,
    OAuthClientType ClientType,
    IReadOnlyList<string> AllowedGrantTypes,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> AllowedScopes,
    DateTimeOffset? ExpiresAt,
    string? CreatedBy);

/// <summary>A registered OAuth2 client (no plaintext secret).</summary>
internal sealed record OAuthClientRecord(
    Guid Id,
    string ClientId,
    OAuthClientType ClientType,
    string Name,
    string? SecretPrefix,
    byte[]? SecretHash,
    IReadOnlyList<string> AllowedGrantTypes,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> AllowedScopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    string? CreatedBy);

/// <summary>Create result carrying the one-time plaintext secret (confidential only).</summary>
internal sealed record OAuthClientCreateResult(OAuthClientRecord Record, string? Secret);

internal sealed class InMemoryOAuthClientStore(TimeProvider? timeProvider = null) : IOAuthClientStore
{
    private const string ClientIdPrefix = "client_";
    private const string SecretPrefix = "secret_";
    private const int IdByteCount = 18;
    private const int SecretByteCount = 32;
    private const int DisplaySecretPrefixLength = 14;

    private readonly ConcurrentDictionary<Guid, OAuthClientRecord> _clients = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<IReadOnlyList<OAuthClientRecord>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<OAuthClientRecord> result = _clients.Values
            .OrderBy(static client => client.CreatedAt)
            .ToList()
            .AsReadOnly();
        return Task.FromResult(result);
    }

    public Task<OAuthClientCreateResult> CreateAsync(
        OAuthClientRegistration registration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var clientId = GenerateOpaque(ClientIdPrefix, IdByteCount);

        // Public clients (RFC 6749 §2.1) cannot keep a secret, so none is minted.
        // Confidential clients get a single-use, SHA-256-hashed-at-rest secret.
        string? secret = null;
        string? secretPrefix = null;
        byte[]? secretHash = null;
        if (registration.ClientType == OAuthClientType.Confidential)
        {
            secret = GenerateOpaque(SecretPrefix, SecretByteCount);
            secretPrefix = CreateDisplayPrefix(secret);
            secretHash = HashSecret(secret);
        }

        var record = new OAuthClientRecord(
            Id: Guid.NewGuid(),
            ClientId: clientId,
            ClientType: registration.ClientType,
            Name: registration.Name,
            SecretPrefix: secretPrefix,
            SecretHash: secretHash,
            AllowedGrantTypes: NormalizeList(registration.AllowedGrantTypes),
            RedirectUris: NormalizeList(registration.RedirectUris),
            AllowedScopes: NormalizeList(registration.AllowedScopes),
            CreatedAt: now,
            UpdatedAt: now,
            ExpiresAt: registration.ExpiresAt,
            LastUsedAt: null,
            RevokedAt: null,
            CreatedBy: registration.CreatedBy);

        if (!_clients.TryAdd(record.Id, record))
        {
            throw new InvalidOperationException("Generated duplicate OAuth client identifier.");
        }

        return Task.FromResult(new OAuthClientCreateResult(record, secret));
    }

    public Task<OAuthClientRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _clients.TryGetValue(id, out var record);
        return Task.FromResult(record);
    }

    public Task<OAuthClientRecord?> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _clients.TryRemove(id, out var record);
        return Task.FromResult(record);
    }

    public Task<OAuthClientRecord?> ValidateSecretAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return Task.FromResult<OAuthClientRecord?>(null);
        }

        var providedHash = HashSecret(clientSecret);
        var now = _timeProvider.GetUtcNow();

        foreach (var record in _clients.Values)
        {
            if (!string.Equals(record.ClientId, clientId, StringComparison.Ordinal))
            {
                continue;
            }

            // A public client carries no secret and therefore cannot authenticate
            // via the secret-bearing client_credentials path.
            if (record.SecretHash is null ||
                record.RevokedAt is not null ||
                (record.ExpiresAt.HasValue && record.ExpiresAt.Value <= now))
            {
                return Task.FromResult<OAuthClientRecord?>(null);
            }

            if (!CryptographicOperations.FixedTimeEquals(providedHash, record.SecretHash))
            {
                return Task.FromResult<OAuthClientRecord?>(null);
            }

            var updated = record with { LastUsedAt = now, UpdatedAt = now };
            _clients[record.Id] = updated;
            return Task.FromResult<OAuthClientRecord?>(updated);
        }

        return Task.FromResult<OAuthClientRecord?>(null);
    }

    private static string[] NormalizeList(IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return [];
        }

        return values
            .Select(static value => value?.Trim() ?? string.Empty)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GenerateOpaque(string prefix, int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return prefix + Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string CreateDisplayPrefix(string secret)
    {
        var length = Math.Min(DisplaySecretPrefixLength, secret.Length);
        return secret[..length];
    }

    private static byte[] HashSecret(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));
}
