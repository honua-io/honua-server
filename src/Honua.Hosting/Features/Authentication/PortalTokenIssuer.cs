// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Infrastructure.Logging;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Distributed-cache backed <see cref="IPortalTokenIssuer"/> mirroring the
/// <see cref="AdminAuthSessionStore"/> dual-tier storage pattern: an in-memory
/// tier accelerates the redis round trip while the distributed tier remains
/// authoritative for multi-node deployments.
/// </summary>
internal sealed partial class PortalTokenIssuer(
    IMemoryCache memoryCache,
    ILogger<PortalTokenIssuer> logger,
    IDistributedCache? distributedCache = null) : IPortalTokenIssuer
{
    internal const string AuthSchemeName = "PortalToken";
    internal const string AuthTypeClaimValue = "portal-token";
    internal const string TenantClaimType = "tenant_id";
    internal const string BindingClaimType = "portal_token_binding";
    private const string TokenKeyPrefix = "portal-auth:token:";

    private readonly IMemoryCache _memoryCache = memoryCache;
    private readonly ILogger<PortalTokenIssuer> _logger = logger;
    private readonly IDistributedCache? _distributedCache = distributedCache;

    /// <inheritdoc />
    public async Task<PortalTokenIssuance> IssueAsync(
        PortalTokenIssueRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = CreateTokenValue();
        var record = new PortalTokenRecord
        {
            PrincipalId = request.PrincipalId,
            DisplayName = request.DisplayName,
            TenantId = request.TenantId,
            Roles = request.Roles.ToArray(),
            ClientType = request.ClientType,
            BindingValue = NormalizeBindingValue(request.ClientType, request.BindingValue),
            ExpiresAt = request.ExpiresAt
        };

        await SetAsync(
            TokenKeyPrefix + token,
            record,
            request.ExpiresAt,
            PortalTokenJsonContext.Default.PortalTokenRecord,
            cancellationToken).ConfigureAwait(false);

        return new PortalTokenIssuance(token, request.ExpiresAt);
    }

    /// <inheritdoc />
    public async Task<PortalTokenValidation?> ValidateAsync(
        string token,
        PortalTokenBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var record = await GetAsync(
            TokenKeyPrefix,
            token,
            PortalTokenJsonContext.Default.PortalTokenRecord,
            cancellationToken).ConfigureAwait(false);

        if (record is null)
        {
            return null;
        }

        if (!BindingMatches(record, binding))
        {
            PortalTokenLog.BindingMismatch(_logger, record.ClientType.ToString(), LogValueRedactor.Hash(token));
            return null;
        }

        var principal = ProjectPrincipal(record);
        return new PortalTokenValidation(principal, record.ExpiresAt);
    }

    private static string CreateTokenValue()
    {
        // 256 bits of entropy matches AdminAuthSessionStore. Hex output keeps the
        // value URL-safe so it can flow through the ?token= query parameter that
        // ArcGIS clients use.
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    private static string NormalizeBindingValue(PortalTokenClientType clientType, string bindingValue)
    {
        if (string.IsNullOrWhiteSpace(bindingValue))
        {
            return string.Empty;
        }

        return clientType switch
        {
            PortalTokenClientType.Referer => NormalizeRefererValue(bindingValue),
            PortalTokenClientType.Ip => bindingValue.Trim(),
            _ => bindingValue.Trim(),
        };
    }

    private static string NormalizeRefererValue(string value)
    {
        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            // ArcGIS binds at the host level: keep scheme + host + port so a token
            // issued for https://app.example.com/foo continues to work for
            // https://app.example.com/bar without leaking through other origins.
            var builder = new UriBuilder(uri.Scheme, uri.Host)
            {
                Port = uri.IsDefaultPort ? -1 : uri.Port,
                Path = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty
            };
            return builder.Uri.ToString().TrimEnd('/');
        }

        return trimmed.TrimEnd('/');
    }

    private static bool BindingMatches(PortalTokenRecord record, PortalTokenBinding binding)
    {
        return record.ClientType switch
        {
            PortalTokenClientType.Referer => string.Equals(
                record.BindingValue,
                NormalizeRefererValue(binding.Referer ?? string.Empty),
                StringComparison.OrdinalIgnoreCase),
            PortalTokenClientType.Ip => string.Equals(
                record.BindingValue,
                (binding.ClientIp ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static ClaimsPrincipal ProjectPrincipal(PortalTokenRecord record)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, record.PrincipalId),
            new(ClaimTypes.NameIdentifier, record.PrincipalId),
            new("auth_type", AuthTypeClaimValue),
            new(BindingClaimType, record.ClientType.ToString()),
        };

        if (!string.IsNullOrWhiteSpace(record.DisplayName))
        {
            claims.Add(new Claim("display_name", record.DisplayName));
        }

        if (!string.IsNullOrWhiteSpace(record.TenantId))
        {
            claims.Add(new Claim(TenantClaimType, record.TenantId!));
        }

        foreach (var role in record.Roles)
        {
            if (!string.IsNullOrWhiteSpace(role))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
        }

        var identity = new ClaimsIdentity(
            claims,
            AuthSchemeName,
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    private async Task SetAsync<T>(
        string key,
        T value,
        DateTimeOffset expiresAt,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        if (_distributedCache is null)
        {
            _memoryCache.Set(key, payload, expiresAt);
            return;
        }

        try
        {
            await _distributedCache.SetAsync(
                key,
                payload,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpiration = expiresAt
                },
                cancellationToken).ConfigureAwait(false);
            _memoryCache.Set(key, payload, expiresAt);
        }
        catch (Exception ex)
        {
            _memoryCache.Remove(key);
            PortalTokenLog.DistributedCachePersistFailed(_logger, LogValueRedactor.Hash(key), ex);
            // Same rationale as AdminAuthSessionStore: surface persistence failures
            // so the caller does not believe a session was issued when another
            // replica will not see it.
            throw;
        }
    }

    private async Task<PortalTokenRecord?> GetAsync(
        string keyPrefix,
        string token,
        JsonTypeInfo<PortalTokenRecord> typeInfo,
        CancellationToken cancellationToken)
    {
        var key = keyPrefix + token;
        byte[]? payload = null;

        if (_distributedCache is not null)
        {
            try
            {
                payload = await _distributedCache.GetAsync(key, cancellationToken).ConfigureAwait(false);
                if (payload is null)
                {
                    _memoryCache.Remove(key);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _memoryCache.Remove(key);
                PortalTokenLog.DistributedCacheReadFailed(_logger, LogValueRedactor.Hash(key), ex);
                return null;
            }
        }
        else if (!_memoryCache.TryGetValue(key, out payload) || payload is null)
        {
            return null;
        }

        var value = JsonSerializer.Deserialize(payload, typeInfo);
        if (value is null)
        {
            await RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (value.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            return null;
        }

        _memoryCache.Set(key, payload, value.ExpiresAt);
        return value;
    }

    private async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        _memoryCache.Remove(key);

        if (_distributedCache is null)
        {
            return;
        }

        try
        {
            await _distributedCache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PortalTokenLog.DistributedCacheRemoveFailed(_logger, LogValueRedactor.Hash(key), ex);
        }
    }
}

internal sealed class PortalTokenRecord
{
    public required string PrincipalId { get; init; }

    public string? DisplayName { get; init; }

    public string? TenantId { get; init; }

    public required string[] Roles { get; init; }

    public required PortalTokenClientType ClientType { get; init; }

    public required string BindingValue { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PortalTokenRecord))]
internal sealed partial class PortalTokenJsonContext : JsonSerializerContext
{
}
