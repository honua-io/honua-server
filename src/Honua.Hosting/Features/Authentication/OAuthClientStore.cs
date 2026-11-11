// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Infrastructure.Authentication;

// The client secret is stored as a SHA-256 hash and compared in constant time.
internal interface IOAuthClientStore
{
    // List results omit the secret.
    Task<IReadOnlyList<OAuthClientRecord>> ListAsync(CancellationToken cancellationToken);

    // The plaintext secret is returned only from registration.
    Task<OAuthClientCreateResult> CreateAsync(
        OAuthClientRegistration registration,
        CancellationToken cancellationToken);

    // A fetched client omits the secret.
    Task<OAuthClientRecord?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<OAuthClientRecord?> DeleteAsync(Guid id, CancellationToken cancellationToken);

    // Callers must not distinguish an unknown client from a bad secret.
    Task<OAuthClientRecord?> ValidateSecretAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken);
}

internal enum OAuthClientType
{
    Confidential = 0,

    // A public client cannot hold a secret, so none is minted.
    Public = 1,
}

internal sealed record OAuthClientRegistration(
    string Name,
    OAuthClientType ClientType,
    IReadOnlyList<string> AllowedGrantTypes,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> AllowedScopes,
    DateTimeOffset? ExpiresAt,
    string? CreatedBy);

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

// The plaintext secret is one-time and only for a confidential client.
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
            if (_clients.TryUpdate(record.Id, updated, record))
            {
                return Task.FromResult<OAuthClientRecord?>(updated);
            }

            continue;
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
