// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Security.ConnectionSecretResolvers;

internal sealed class AzureKeyVaultResolver : IConnectionSecretResolver, IDisposable
{
    private const string ProviderType = "azure";
    private const string KeyVaultPrefix = "azure:keyvault:";
    private const string TokenResource = "https://vault.azure.net";
    private const string TokenScope = "https://vault.azure.net/.default";
    private const string DefaultApiVersion = "7.4";
    private const string ClientName = "AzureKeyVault";
    private const string ManagedIdentityClientName = "AzureManagedIdentityMetadata";
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);
    private static readonly Regex _vaultNamePattern = new("^[A-Za-z0-9-]{3,24}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex _secretNamePattern = new("^[A-Za-z0-9-]{1,127}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex _secretVersionPattern = new("^[A-Za-z0-9]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Action<ILogger, Exception?> _logManagedIdentityTokenFailed =
        LoggerMessage.Define(LogLevel.Debug, new EventId(6402, "AzureManagedIdentityTokenRequestFailed"), "Azure Managed Identity token request failed");

    private readonly HttpClient _httpClient;
    private readonly HttpClient _metadataClient;
    private readonly ILogger<AzureKeyVaultResolver> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private bool _disposed;

    private AccessTokenCache? _tokenCache;

    public AzureKeyVaultResolver(IHttpClientFactory httpClientFactory, ILogger<AzureKeyVaultResolver> logger)
    {
        _httpClient = httpClientFactory.CreateClient(ClientName);
        _metadataClient = httpClientFactory.CreateClient(ManagedIdentityClientName);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> ResolveConnectionStringAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);

        if (!secretRef.StartsWith(KeyVaultPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid Azure secret reference. Expected '{KeyVaultPrefix}<vault>:<secret>[:<version>]'.", nameof(secretRef));
        }

        if (TryGetCached(secretRef, out var cached))
        {
            return cached;
        }

        var (vaultName, secretName, version) = ParseSecretReference(secretRef);
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        var url = BuildSecretUrl(vaultName, secretName, version);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Azure Key Vault request failed with status code {(int)response.StatusCode}.");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var secretValue = ExtractSecretValue(content);
        CacheValue(secretRef, secretValue);
        return secretValue;
    }

    public Task<bool> CanResolveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretRef) ||
            !secretRef.StartsWith(KeyVaultPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(false);
        }

        try
        {
            var parsed = ParseSecretReference(secretRef);
            return Task.FromResult(!string.IsNullOrWhiteSpace(parsed.VaultName) && !string.IsNullOrWhiteSpace(parsed.SecretName));
        }
        catch (ArgumentException)
        {
            return Task.FromResult(false);
        }
    }

    public string[] GetSupportedProviders() => new[] { ProviderType };

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (TryGetCachedToken(out var token))
        {
            return token;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCachedToken(out token))
            {
                return token;
            }

            if (TryGetClientCredentials(out var tenantId, out var clientId, out var clientSecret))
            {
                var tokenResponse = await RequestClientCredentialsTokenAsync(tenantId, clientId, clientSecret, cancellationToken)
                    .ConfigureAwait(false);
                UpdateTokenCache(tokenResponse);
                return tokenResponse.AccessToken;
            }

            var managedIdentityToken = await RequestManagedIdentityTokenAsync(cancellationToken).ConfigureAwait(false);
            if (managedIdentityToken != null)
            {
                UpdateTokenCache(managedIdentityToken);
                return managedIdentityToken.AccessToken;
            }

            throw new InvalidOperationException("Azure credentials are not configured. Set AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET or enable Managed Identity.");
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static bool TryGetClientCredentials(out string tenantId, out string clientId, out string clientSecret)
    {
        tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID") ?? string.Empty;
        clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID") ?? string.Empty;
        clientSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET") ?? string.Empty;

        return !string.IsNullOrWhiteSpace(tenantId) &&
               !string.IsNullOrWhiteSpace(clientId) &&
               !string.IsNullOrWhiteSpace(clientSecret);
    }

    private async Task<TokenResponse> RequestClientCredentialsTokenAsync(
        string tenantId,
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        var tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "client_credentials",
            ["scope"] = TokenScope
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Azure token request failed with status code {(int)response.StatusCode}.");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseTokenResponse(content);
    }

    private async Task<TokenResponse?> RequestManagedIdentityTokenAsync(CancellationToken cancellationToken)
    {
        var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var query = $"api-version=2018-02-01&resource={Uri.EscapeDataString(TokenResource)}";
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            query += $"&client_id={Uri.EscapeDataString(clientId)}";
        }

        var uri = $"http://169.254.169.254/metadata/identity/oauth2/token?{query}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Metadata", "true");

        try
        {
            using var response = await _metadataClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseTokenResponse(content);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logManagedIdentityTokenFailed(_logger, ex);
            return null;
        }
    }

    private static TokenResponse ParseTokenResponse(string content)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var tokenElement)
            ? tokenElement.GetString()
            : null;
        var expiresIn = root.TryGetProperty("expires_in", out var expiresElement) && expiresElement.TryGetInt32(out var seconds)
            ? seconds
            : 3600;

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Azure token response did not include access_token.");
        }

        return new TokenResponse(accessToken!, expiresIn);
    }

    private bool TryGetCachedToken(out string token)
    {
        if (_tokenCache is { } cache &&
            cache.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            token = cache.AccessToken;
            return true;
        }

        token = string.Empty;
        return false;
    }

    private void UpdateTokenCache(TokenResponse response)
    {
        _tokenCache = new AccessTokenCache(
            response.AccessToken,
            DateTimeOffset.UtcNow.AddSeconds(response.ExpiresInSeconds));
    }

    private static string BuildSecretUrl(string vaultName, string secretName, string? version)
    {
        var escapedSecretName = Uri.EscapeDataString(secretName);
        var escapedVersion = string.IsNullOrWhiteSpace(version)
            ? null
            : Uri.EscapeDataString(version);

        var versionSegment = string.IsNullOrWhiteSpace(escapedVersion) ? string.Empty : $"/{escapedVersion}";
        return $"https://{vaultName}.vault.azure.net/secrets/{escapedSecretName}{versionSegment}?api-version={DefaultApiVersion}";
    }

    private static (string VaultName, string SecretName, string? Version) ParseSecretReference(string secretRef)
    {
        var trimmed = secretRef[KeyVaultPrefix.Length..];
        var parts = trimmed.Split(':', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            throw new ArgumentException($"Invalid Azure secret reference. Expected '{KeyVaultPrefix}<vault>:<secret>[:<version>]'.", nameof(secretRef));
        }

        var vaultName = parts[0];
        var secretName = parts[1];
        var version = parts.Length == 3 ? parts[2] : null;

        if (!IsValidVaultName(vaultName))
        {
            throw new ArgumentException("Invalid vault name in Azure secret reference.", nameof(secretRef));
        }

        if (!IsValidSecretName(secretName))
        {
            throw new ArgumentException("Invalid secret name in Azure secret reference.", nameof(secretRef));
        }

        if (!string.IsNullOrWhiteSpace(version) && !IsValidSecretVersion(version))
        {
            throw new ArgumentException("Invalid secret version in Azure secret reference.", nameof(secretRef));
        }

        return (vaultName, secretName, version);
    }

    internal static bool IsValidVaultName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !_vaultNamePattern.IsMatch(value))
        {
            return false;
        }

        return value[0] != '-' && value[^1] != '-';
    }

    internal static bool IsValidSecretName(string value) =>
        !string.IsNullOrWhiteSpace(value) && _secretNamePattern.IsMatch(value);

    internal static bool IsValidSecretVersion(string value) =>
        !string.IsNullOrWhiteSpace(value) && _secretVersionPattern.IsMatch(value);

    private static string ExtractSecretValue(string content)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (!root.TryGetProperty("value", out var valueElement) ||
            valueElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Azure Key Vault response did not include a value.");
        }

        return SecretValueExtractor.ExtractValue(valueElement.GetString()!);
    }

    private bool TryGetCached(string secretRef, out string value)
    {
        if (_cache.TryGetValue(secretRef, out var entry) &&
            entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            value = entry.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private void CacheValue(string secretRef, string value)
    {
        _cache[secretRef] = new CacheEntry(value, DateTimeOffset.UtcNow.Add(_cacheTtl));
    }

    private sealed record CacheEntry(string Value, DateTimeOffset ExpiresAt);

    private sealed record TokenResponse(string AccessToken, int ExpiresInSeconds);

    private sealed record AccessTokenCache(string AccessToken, DateTimeOffset ExpiresAt);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _tokenLock.Dispose();
        _disposed = true;
    }
}
